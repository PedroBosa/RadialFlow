// ============================================================================
// RadialFlow - Leitor de linhas da saída de um processo
// ----------------------------------------------------------------------------
// Lê um Stream (pipe) até o fim e entrega cada linha a um callback.
//   * UTF-8 com decoder de estado: um caractere multibyte partido entre duas
//     leituras sai inteiro; bytes inválidos viram U+FFFD (nunca lança).
//   * "\n", "\r" e "\r\n" terminam a linha. O "\r" sozinho importa: o ffmpeg
//     atualiza o progresso reescrevendo a mesma linha com "\r".
//   * Memória limitada: uma "linha" sem terminador (saída binária) é cortada em
//     pedaços de no máximo maxLineLength caracteres.
//   * Exceção do callback é registrada e a leitura continua: se parasse de
//     ler, o pipe encheria e o processo ficaria bloqueado para sempre.
// Bloqueante: roda em thread dedicada (ver UnelevatedLauncher).
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using RadialFlow.Infrastructure;

namespace RadialFlow.Services;

internal static class OutputLineReader
{
    internal const int DefaultMaxLineLength = 16 * 1024;
    internal const int DefaultBufferSize = 4096;

    /// <param name="stream">Pipe de leitura (lido até EOF; não é fechado aqui).</param>
    /// <param name="onLine">Recebe cada linha, sem o terminador (linhas vazias incluídas).</param>
    /// <param name="maxLineLength">Tamanho máximo de uma linha em caracteres (mínimo 1).</param>
    /// <param name="bufferSize">Bytes lidos por vez (mínimo 1). Pequeno só nos testes.</param>
    public static void Pump(Stream stream, Action<string> onLine,
        int maxLineLength = DefaultMaxLineLength, int bufferSize = DefaultBufferSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(onLine);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);

        Decoder decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetDecoder();
        byte[] bytes = new byte[bufferSize];
        char[] chars = new char[Encoding.UTF8.GetMaxCharCount(bufferSize)];
        var line = new StringBuilder();
        bool skipLineFeed = false;   // o "\n" logo depois de um "\r" faz parte do mesmo terminador

        void Emit()
        {
            string text = line.ToString();
            line.Clear();
            try
            {
                onLine(text);
            }
            catch (Exception ex)
            {
                AppLog.Warning("Launcher", $"Callback de linha falhou: {ex.Message}");
            }
        }

        void Consume(ReadOnlySpan<char> text)
        {
            foreach (char c in text)
            {
                if (skipLineFeed)
                {
                    skipLineFeed = false;
                    if (c == '\n') continue;
                }

                if (c is '\r' or '\n')
                {
                    Emit();
                    skipLineFeed = c == '\r';
                    continue;
                }

                // Corta ANTES de acrescentar: uma linha com exatamente maxLineLength caracteres seguida de
                // terminador sai inteira, sem uma linha vazia sobrando.
                if (line.Length >= maxLineLength) Emit();
                line.Append(c);
            }
        }

        int read;
        while ((read = stream.Read(bytes, 0, bytes.Length)) > 0)
        {
            int count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
            Consume(chars.AsSpan(0, count));
        }

        // Fim do fluxo: descarrega bytes pendentes do decoder (sequência incompleta) e o resto da linha.
        int tail = decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, flush: true);
        Consume(chars.AsSpan(0, tail));
        if (line.Length > 0) Emit();
    }
}
