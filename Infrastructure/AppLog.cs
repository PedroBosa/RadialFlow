// ============================================================================
// RadialFlow - Log em arquivo
// ----------------------------------------------------------------------------
// Antes: 85 chamadas a Debug.WriteLine, que NÃO FAZEM NADA em Release. Falhas do
// hook, de ações e de ferramentas não deixavam rastro.
//
// Garantias (o hook do mouse e as ações chamam isto, então precisa ser seguro):
//   * quem loga NUNCA bloqueia e NUNCA recebe exceção: a entrada vai para uma fila
//     limitada (se encher, descarta a mais antiga) e uma thread dedicada escreve;
//   * nada é formatado quando o nível está desligado;
//   * rotação por tamanho (radialflow.log → .1.log ... .4.log), então o disco não enche;
//   * falha de disco é tolerada: depois de 5 falhas seguidas a escrita é desligada
//     (sem derrubar o app); uma falha isolada só perde aquela entrada.
//
// Local: %LocalAppData%\RadialFlow\logs\radialflow.log
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;

namespace RadialFlow.Infrastructure;

public enum LogLevel { Debug = 0, Info = 1, Warning = 2, Error = 3 }

/// <summary>Escritor de log em arquivo com fila, rotação e encerramento ordenado. Thread-safe.</summary>
internal sealed class FileLogSink : IDisposable
{
    private const string FlushToken = "\0flush";
    private const int MaxConsecutiveFailures = 5;

    private readonly record struct Entry(DateTime Timestamp, LogLevel Level, int ThreadId, string Source, string Message, string? Exception);

    private readonly string _directory;
    private readonly string _fileName;
    private readonly long _maxBytes;
    private readonly int _keepFiles;
    private readonly Channel<Entry> _queue;
    private readonly Thread _writer;
    private readonly ManualResetEventSlim _drained = new(false);

    // Flush: o marcador entra na fila JUNTO com a sentinela (sob o mesmo lock), preservando a ordem.
    private readonly object _flushGate = new();
    private readonly Queue<ManualResetEventSlim> _flushMarkers = new();
    private int _disposed;

    /// <param name="directory">Pasta dos logs (criada se necessário).</param>
    /// <param name="fileName">Nome do arquivo ativo, ex.: radialflow.log.</param>
    /// <param name="maxBytes">Tamanho que dispara a rotação.</param>
    /// <param name="keepFiles">Quantos arquivos rotacionados guardar (além do ativo).</param>
    /// <param name="capacity">Tamanho da fila; cheia, descarta a entrada mais antiga.</param>
    public FileLogSink(string directory, string fileName = "radialflow.log", long maxBytes = 1_000_000, int keepFiles = 4, int capacity = 2_000)
    {
        _directory = directory;
        _fileName = fileName;
        _maxBytes = Math.Max(1_024, maxBytes);
        _keepFiles = Math.Max(1, keepFiles);

        _queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(Math.Max(16, capacity))
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        _writer = new Thread(WriterLoop) { Name = "RadialFlow.Log", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _writer.Start();
    }

    public string ActiveFilePath => Path.Combine(_directory, _fileName);

    /// <summary>Enfileira uma entrada. Nunca bloqueia nem lança.</summary>
    public void Write(LogLevel level, string source, string message, Exception? exception = null)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        try
        {
            _queue.Writer.TryWrite(new Entry(DateTime.Now, level, Environment.CurrentManagedThreadId, source, message, exception?.ToString()));
        }
        catch
        {
            // um logger nunca derruba quem o chama
        }
    }

    /// <summary>Espera tudo o que foi enfileirado até agora chegar ao arquivo (ou o prazo acabar).</summary>
    public bool Flush(TimeSpan timeout)
    {
        if (Volatile.Read(ref _disposed) != 0) return true;

        var marker = new ManualResetEventSlim(false);   // sem WaitHandle: não ocupa handle do SO, não precisa de Dispose
        lock (_flushGate)
        {
            _flushMarkers.Enqueue(marker);
            _queue.Writer.TryWrite(new Entry(DateTime.MinValue, LogLevel.Debug, 0, "", FlushToken, null));
        }

        return marker.Wait(timeout);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _queue.Writer.TryComplete();
        _drained.Wait(TimeSpan.FromSeconds(3));
        _drained.Dispose();
    }

    // ------------------------------------------------------------------
    // Thread de escrita
    // ------------------------------------------------------------------

    private void WriterLoop()
    {
        StreamWriter? writer = null;
        long size = 0;
        int failures = 0;

        try
        {
            ChannelReader<Entry> reader = _queue.Reader;
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out Entry entry))
                {
                    if (entry.Message == FlushToken)
                    {
                        try { writer?.Flush(); } catch (IOException) { /* a próxima escrita tenta de novo */ }
                        ReleaseOneFlushMarker();
                        continue;
                    }

                    if (failures >= MaxConsecutiveFailures) continue;   // disco indisponível: descarta em silêncio

                    try
                    {
                        string text = Format(entry.Timestamp, entry.Level, entry.ThreadId, entry.Source, entry.Message, entry.Exception);

                        if (writer is null)
                        {
                            Directory.CreateDirectory(_directory);
                            writer = OpenActive(out size);
                        }

                        if (size > 0 && size + text.Length > _maxBytes)
                        {
                            writer.Dispose();
                            writer = null;
                            Rotate();
                            writer = OpenActive(out size);
                        }

                        writer.Write(text);
                        size += Encoding.UTF8.GetByteCount(text);
                        failures = 0;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Perde só esta entrada; fecha o arquivo para reabri-lo (e rotacioná-lo) na próxima.
                        failures++;
                        try { writer?.Dispose(); } catch { /* ignorado */ }
                        writer = null;
                        Trace.WriteLine($"[RadialFlow][Log] Falha de escrita ({failures}/{MaxConsecutiveFailures}): {ex.Message}");
                    }
                }

                try { writer?.Flush(); } catch (IOException) { /* ignorado */ }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[RadialFlow][Log] Thread de log encerrou: {ex.Message}");
        }
        finally
        {
            try { writer?.Dispose(); } catch { /* ignorado */ }
            while (ReleaseOneFlushMarker()) { }   // ninguém fica esperando um Flush que não virá
            _drained.Set();
        }
    }

    private bool ReleaseOneFlushMarker()
    {
        lock (_flushGate)
        {
            if (!_flushMarkers.TryDequeue(out ManualResetEventSlim? marker)) return false;
            marker.Set();
            return true;
        }
    }

    private StreamWriter OpenActive(out long size)
    {
        var stream = new FileStream(ActiveFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        size = stream.Length;
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = false };
    }

    /// <summary>radialflow.log → radialflow.1.log → ... → radialflow.N.log (o mais antigo é apagado).</summary>
    private void Rotate()
    {
        string baseName = Path.GetFileNameWithoutExtension(_fileName);
        string extension = Path.GetExtension(_fileName);
        string Numbered(int n) => Path.Combine(_directory, $"{baseName}.{n}{extension}");

        if (File.Exists(Numbered(_keepFiles))) File.Delete(Numbered(_keepFiles));
        for (int i = _keepFiles - 1; i >= 1; i--)
        {
            if (File.Exists(Numbered(i))) File.Move(Numbered(i), Numbered(i + 1), overwrite: true);
        }

        if (File.Exists(ActiveFilePath)) File.Move(ActiveFilePath, Numbered(1), overwrite: true);
    }

    /// <summary>"2026-10-24 17:50:12.345 [WRN] t07 Fonte: mensagem" (+ exceção indentada).</summary>
    internal static string Format(DateTime timestamp, LogLevel level, int threadId, string source, string message, string? exception)
    {
        string tag = level switch { LogLevel.Debug => "DBG", LogLevel.Info => "INF", LogLevel.Warning => "WRN", _ => "ERR" };
        var builder = new StringBuilder(128);
        builder.Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
               .Append(" [").Append(tag).Append("] t").Append(threadId.ToString("00", CultureInfo.InvariantCulture))
               .Append(' ').Append(source).Append(": ").Append(message).Append('\n');

        if (!string.IsNullOrEmpty(exception))
        {
            foreach (string line in exception.Split('\n'))
                builder.Append("    ").Append(line.TrimEnd('\r')).Append('\n');
        }

        return builder.ToString();
    }
}

/// <summary>Fachada estática do log do aplicativo.</summary>
public static class AppLog
{
    private static readonly Lazy<FileLogSink> Sink = new(() => new FileLogSink(DirectoryPath), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>%LocalAppData%\RadialFlow\logs</summary>
    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadialFlow", "logs");

    /// <summary>Arquivo ativo do log.</summary>
    public static string FilePath => Path.Combine(DirectoryPath, "radialflow.log");

    /// <summary>Nível mínimo gravado. Depuração só aparece em builds Debug ou com a variável RADIALFLOW_LOG=debug.</summary>
    public static LogLevel MinimumLevel { get; set; } = DefaultLevel();

    private static LogLevel DefaultLevel()
    {
#if DEBUG
        return LogLevel.Debug;
#else
        return string.Equals(Environment.GetEnvironmentVariable("RADIALFLOW_LOG"), "debug", StringComparison.OrdinalIgnoreCase)
            ? LogLevel.Debug
            : LogLevel.Info;
#endif
    }

    public static void Debug(string source, string message) => Write(LogLevel.Debug, source, message, null);
    public static void Info(string source, string message) => Write(LogLevel.Info, source, message, null);
    public static void Warning(string source, string message, Exception? exception = null) => Write(LogLevel.Warning, source, message, exception);
    public static void Error(string source, string message, Exception? exception = null) => Write(LogLevel.Error, source, message, exception);

    /// <summary>Nível ligado? Permite evitar montar mensagens caras quando o nível está desligado.</summary>
    public static bool IsEnabled(LogLevel level) => level >= MinimumLevel;

    private static void Write(LogLevel level, string source, string message, Exception? exception)
    {
        if (level < MinimumLevel) return;

        // Janela do depurador (só builds Debug): o comportamento anterior, preservado.
        System.Diagnostics.Debug.WriteLine($"[RadialFlow][{source}] {message}{(exception is null ? "" : " :: " + exception.Message)}");

        try
        {
            Sink.Value.Write(level, source, message, exception);
        }
        catch
        {
            // um logger nunca derruba quem o chama
        }
    }

    /// <summary>Grava o que estiver na fila e fecha o arquivo. Chamar ao encerrar o aplicativo.</summary>
    public static void Shutdown()
    {
        if (!Sink.IsValueCreated) return;
        try { Sink.Value.Dispose(); } catch { /* ignorado */ }
    }

    /// <summary>Espera a fila esvaziar (até o prazo).</summary>
    public static bool Flush(TimeSpan timeout) => !Sink.IsValueCreated || Sink.Value.Flush(timeout);
}
