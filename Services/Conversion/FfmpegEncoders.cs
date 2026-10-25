// ============================================================================
// RadialFlow - Codificadores do FFmpeg instalado
// ----------------------------------------------------------------------------
// Builds do FFmpeg variam (a "essentials" não tem tudo da "full"). Para só
// oferecer o que funciona, consulta "ffmpeg -encoders" uma vez por executável,
// SEM elevação (UnelevatedLauncher), em segundo plano. Enquanto a resposta não
// chega, TryGet devolve null e o catálogo assume os codificadores comuns.
// Codificadores experimentais (flag X, ex.: o "opus" nativo) são ignorados:
// exigiriam -strict experimental.
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RadialFlow.Infrastructure;

namespace RadialFlow.Services;

internal static class FfmpegEncoders
{
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);

    private sealed record Entry(DateTime ExeWriteTimeUtc, DateTime StartedUtc, Task<IReadOnlySet<string>?> Query);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Codificadores já conhecidos; null se a consulta ainda não terminou (ela é iniciada aqui).</summary>
    public static IReadOnlySet<string>? TryGet(string ffmpegPath)
    {
        Task<IReadOnlySet<string>?> query = GetQuery(ffmpegPath);
        return query.IsCompletedSuccessfully ? query.Result : null;
    }

    /// <summary>Espera a consulta (usada ao converter, quando os argumentos precisam ser exatos).</summary>
    public static Task<IReadOnlySet<string>?> GetAsync(string ffmpegPath) => GetQuery(ffmpegPath);

    private static Task<IReadOnlySet<string>?> GetQuery(string ffmpegPath)
    {
        DateTime writeTime;
        try { writeTime = File.GetLastWriteTimeUtc(ffmpegPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { writeTime = default; }

        lock (Gate)
        {
            if (Entries.TryGetValue(ffmpegPath, out Entry? entry) && entry.ExeWriteTimeUtc == writeTime)
            {
                bool failed = entry.Query.IsCompleted && (!entry.Query.IsCompletedSuccessfully || entry.Query.Result is null);
                if (!failed || DateTime.UtcNow - entry.StartedUtc < RetryAfterFailure) return entry.Query;
            }

            var query = Task.Run(() => QueryAsync(ffmpegPath));
            Entries[ffmpegPath] = new Entry(writeTime, DateTime.UtcNow, query);
            return query;
        }
    }

    private static async Task<IReadOnlySet<string>?> QueryAsync(string ffmpegPath)
    {
        var lines = new ConcurrentQueue<string>();
        using var timeout = new CancellationTokenSource(QueryTimeout);

        try
        {
            int exitCode = await UnelevatedLauncher.ExecuteAsync(ffmpegPath, new UnelevatedProcessOptions
            {
                Arguments = ["-hide_banner", "-encoders"],
                OnOutputLine = lines.Enqueue
            }, timeout.Token).ConfigureAwait(false);

            IReadOnlySet<string> encoders = Parse(lines);
            if (exitCode != 0 || encoders.Count == 0)
            {
                AppLog.Warning("Conversion", $"O FFmpeg não listou os codificadores (código {exitCode}).");
                return null;
            }

            AppLog.Info("Conversion", $"FFmpeg: {encoders.Count} codificadores disponíveis.");
            return encoders;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warning("Conversion", $"Falha ao consultar os codificadores do FFmpeg: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Lê a saída de "ffmpeg -encoders": depois da linha " ------", cada linha é " FLAGS nome descrição"
    /// (6 flags; a 4ª é X quando o codificador é experimental).
    /// </summary>
    internal static IReadOnlySet<string> Parse(IEnumerable<string> lines)
    {
        var encoders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool inList = false;

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (!inList)
            {
                inList = line.StartsWith("------", StringComparison.Ordinal);
                continue;
            }

            string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0].Length != 6) continue;
            if (parts[0][0] is not ('V' or 'A' or 'S')) continue;
            if (parts[0][3] == 'X') continue;   // experimental

            encoders.Add(parts[1]);
        }

        return encoders;
    }
}
