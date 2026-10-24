using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RadialFlow.Infrastructure;
using RadialFlow.Tests.TestSupport;
using Xunit;

namespace RadialFlow.Tests;

public sealed class FileLogSinkTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private FileLogSink Create(long maxBytes = 1_000_000, int keep = 4, int capacity = 2_000) =>
        new(_temp.Combine("logs"), "teste.log", maxBytes, keep, capacity);

    /// <summary>Lê o log com acesso compartilhado: o logger mantém o arquivo aberto para escrita.</summary>
    private string[] ReadActive()
    {
        using var stream = new FileStream(_temp.Combine("logs", "teste.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    // ------------------------------------------------------------------
    // Formato
    // ------------------------------------------------------------------

    [Fact]
    public void Format_TemHoraNivelThreadFonteEMensagem()
    {
        string line = FileLogSink.Format(new DateTime(2026, 10, 24, 17, 50, 12, 345), LogLevel.Warning, 7, "Hook", "Callback falhou", null);

        Assert.Equal("2026-10-24 17:50:12.345 [WRN] t07 Hook: Callback falhou\n", line);
    }

    [Theory]
    [InlineData(LogLevel.Debug, "DBG")]
    [InlineData(LogLevel.Info, "INF")]
    [InlineData(LogLevel.Warning, "WRN")]
    [InlineData(LogLevel.Error, "ERR")]
    public void Format_RotulosDeNivel(LogLevel level, string tag)
    {
        Assert.Contains($"[{tag}]", FileLogSink.Format(DateTime.Now, level, 1, "X", "m", null));
    }

    [Fact]
    public void Format_ExcecaoVemIndentadaAbaixoDaMensagem()
    {
        string text = FileLogSink.Format(DateTime.Now, LogLevel.Error, 1, "Acao", "Falhou", "System.IO.IOException: disco cheio\r\n   at X.Y()");

        string[] lines = text.TrimEnd('\n').Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("    System.IO.IOException", lines[1]);
        Assert.StartsWith("       at X.Y()", lines[2]);
        Assert.DoesNotContain('\r', text);
    }

    // ------------------------------------------------------------------
    // Escrita
    // ------------------------------------------------------------------

    [Fact]
    public void Write_CriaAPastaEGravaNaOrdem()
    {
        using (FileLogSink sink = Create())
        {
            sink.Write(LogLevel.Info, "A", "primeira");
            sink.Write(LogLevel.Warning, "B", "segunda");
            sink.Write(LogLevel.Error, "C", "terceira", new InvalidOperationException("boom"));
            Assert.True(sink.Flush(TimeSpan.FromSeconds(5)));
        }

        string[] lines = ReadActive();
        Assert.Contains("A: primeira", lines[0]);
        Assert.Contains("B: segunda", lines[1]);
        Assert.Contains("C: terceira", lines[2]);
        Assert.Contains(lines, l => l.Contains("InvalidOperationException: boom"));
    }

    [Fact]
    public void Flush_GarantequeTudoEnfileiradoAntesJaEstaNoArquivo()
    {
        using FileLogSink sink = Create();
        for (int i = 0; i < 200; i++) sink.Write(LogLevel.Info, "Teste", $"linha {i}");

        Assert.True(sink.Flush(TimeSpan.FromSeconds(5)));

        // Sem Dispose: o arquivo já tem as 200 linhas (leitura compartilhada).
        using var stream = new FileStream(sink.ActiveFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        Assert.Equal(200, reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Write_UsoConcorrente_NaoPerdeNemMisturaLinhas()
    {
        using (FileLogSink sink = Create())
        {
            Parallel.For(0, 8, thread =>
            {
                for (int i = 0; i < 250; i++) sink.Write(LogLevel.Info, $"T{thread}", $"mensagem {i} do thread {thread}");
            });
            Assert.True(sink.Flush(TimeSpan.FromSeconds(10)));
        }

        string[] lines = ReadActive();
        Assert.Equal(2_000, lines.Length);
        Assert.All(lines, l => Assert.Matches(@"^\d{4}-\d{2}-\d{2} .* \[INF\] t\d+ T\d: mensagem \d+ do thread \d$", l));
    }

    [Fact]
    public void Dispose_GravaOQueRestavaNaFila()
    {
        FileLogSink sink = Create();
        for (int i = 0; i < 50; i++) sink.Write(LogLevel.Info, "X", $"m{i}");

        sink.Dispose();

        Assert.Equal(50, ReadActive().Length);
    }

    [Fact]
    public void Write_DepoisDoDispose_NaoLancaENaoGrava()
    {
        FileLogSink sink = Create();
        sink.Write(LogLevel.Info, "X", "antes");
        sink.Dispose();

        sink.Write(LogLevel.Info, "X", "depois");   // não lança

        Assert.Single(ReadActive());
        sink.Dispose();   // idempotente
    }

    // ------------------------------------------------------------------
    // Rotação
    // ------------------------------------------------------------------

    [Fact]
    public void Rotacao_PassaoArquivoAtivoParaNumerado_EOLimiteDeArquivosEhRespeitado()
    {
        using (FileLogSink sink = Create(maxBytes: 2_000, keep: 3))
        {
            string filler = new('x', 300);
            for (int i = 0; i < 120; i++) sink.Write(LogLevel.Info, "Rot", $"{i:000} {filler}");
            Assert.True(sink.Flush(TimeSpan.FromSeconds(10)));
        }

        string[] files = Directory.GetFiles(_temp.Combine("logs")).Select(Path.GetFileName).OrderBy(n => n).ToArray()!;

        Assert.Equal(["teste.1.log", "teste.2.log", "teste.3.log", "teste.log"], files);   // nunca passa de ativo + 3
        Assert.All(Directory.GetFiles(_temp.Combine("logs")), f => Assert.True(new FileInfo(f).Length <= 2_000 + 400, $"{f} passou do limite"));
    }

    [Fact]
    public void Rotacao_OsArquivosMaisNovosFicamComOsNumerosMenores()
    {
        using (FileLogSink sink = Create(maxBytes: 2_000, keep: 2))
        {
            string filler = new('y', 300);
            for (int i = 0; i < 40; i++) sink.Write(LogLevel.Info, "Ord", $"{i:000} {filler}");
            Assert.True(sink.Flush(TimeSpan.FromSeconds(10)));
        }

        int Last(string name) => int.Parse(File.ReadAllLines(_temp.Combine("logs", name)).Last(l => l.Contains("Ord:")).Split("Ord: ")[1][..3]);

        Assert.True(Last("teste.log") > Last("teste.1.log"));
        Assert.True(Last("teste.1.log") > Last("teste.2.log"));
    }

    [Fact]
    public void ArquivoExistente_ContinuaDeOndeParou_SemApagar()
    {
        using (FileLogSink first = Create()) first.Write(LogLevel.Info, "Sessao", "um");
        using (FileLogSink second = Create()) second.Write(LogLevel.Info, "Sessao", "dois");

        string[] lines = ReadActive();
        Assert.Equal(2, lines.Length);
        Assert.Contains("um", lines[0]);
        Assert.Contains("dois", lines[1]);
    }

    // ------------------------------------------------------------------
    // Robustez: um logger nunca derruba quem o chama
    // ------------------------------------------------------------------

    [Fact]
    public void PastaImpossivel_NaoLancaENaoTravaOFlush()
    {
        // O "diretório" é um arquivo existente: criar a pasta falha.
        string blocker = _temp.CreateFile("bloqueio");
        using var sink = new FileLogSink(Path.Combine(blocker, "logs"), "x.log");

        for (int i = 0; i < 20; i++) sink.Write(LogLevel.Error, "Falha", "isto não pode ser gravado");

        Assert.True(sink.Flush(TimeSpan.FromSeconds(5)));   // não pendura
    }

    [Fact]
    public void ArquivoTravadoPorOutroProcesso_PerdeSoAsEntradas_ERetomaDepois()
    {
        string logs = _temp.CreateDirectory("logs");
        string active = Path.Combine(logs, "teste.log");
        File.WriteAllText(active, "");

        // A trava existe ANTES de o logger abrir o arquivo (ex.: antivírus ou outro programa lendo o log).
        var locker = new FileStream(active, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var sink = new FileLogSink(logs, "teste.log");
        try
        {
            sink.Write(LogLevel.Info, "Trava", "perdida");
            Assert.True(sink.Flush(TimeSpan.FromSeconds(5)));   // não pendura nem lança
        }
        finally
        {
            locker.Dispose();
        }

        sink.Write(LogLevel.Info, "Trava", "gravada depois");
        Assert.True(sink.Flush(TimeSpan.FromSeconds(5)));

        string[] lines = ReadActive();
        Assert.Contains(lines, l => l.Contains("gravada depois"));
        Assert.DoesNotContain(lines, l => l.Contains("perdida"));
    }

    [Fact]
    public void FilaPequenaECheia_DescartaAsMaisAntigasSemBloquear()
    {
        using var sink = new FileLogSink(_temp.Combine("logs"), "teste.log", capacity: 16);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 50_000; i++) sink.Write(LogLevel.Debug, "Rajada", $"{i}");
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"enfileirar 50 mil entradas levou {stopwatch.Elapsed}");
        Assert.True(sink.Flush(TimeSpan.FromSeconds(10)));
    }

    // ------------------------------------------------------------------
    // Fachada
    // ------------------------------------------------------------------

    [Fact]
    public void AppLog_NosTestes_GravaForaDoLogRealDoUsuario()
    {
        string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadialFlow", "logs");

        Assert.NotNull(AppLog.DirectoryOverride);                    // TestLogIsolation redireciona
        Assert.NotEqual(real, AppLog.DirectoryPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(AppLog.DirectoryPath, "radialflow.log"), AppLog.FilePath);
    }

    [Fact]
    public void AppLog_SemRedirecionamento_UsaLocalAppDataRadialFlowLogs()
    {
        string? original = AppLog.DirectoryOverride;
        try
        {
            AppLog.DirectoryOverride = null;
            string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadialFlow", "logs");

            Assert.Equal(expected, AppLog.DirectoryPath);
            Assert.Equal(Path.Combine(expected, "radialflow.log"), AppLog.FilePath);
        }
        finally
        {
            AppLog.DirectoryOverride = original;
        }
    }

    [Fact]
    public void AppLog_NivelDesligado_NaoEnfileira()
    {
        LogLevel original = AppLog.MinimumLevel;
        try
        {
            AppLog.MinimumLevel = LogLevel.Error;

            Assert.False(AppLog.IsEnabled(LogLevel.Warning));
            Assert.True(AppLog.IsEnabled(LogLevel.Error));
        }
        finally
        {
            AppLog.MinimumLevel = original;
        }
    }
}
