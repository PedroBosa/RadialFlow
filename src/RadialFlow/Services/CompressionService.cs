// ============================================================================
// RadialFlow - Serviço de Compactação e Descompactação ZIP
// ----------------------------------------------------------------------------
// System.IO.Compression com cancelamento e progresso.
// Correções de segurança/robustez:
//   * ZIP SLIP: toda entrada extraída é resolvida com GetFullPath e precisa
//     ficar DENTRO da pasta de destino (o app é administrador).
//   * Zip parcial é apagado se a compactação for cancelada ou falhar.
//   * Pastas vazias entram no zip.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using RadialFlow.Infrastructure;

namespace RadialFlow.Services;

public readonly record struct ProgressReport(double Percentage, string StatusText);

public static class CompressionService
{
    private const int BufferSize = 81920; // 80 KB por bloco

    // Limite do formato ZIP para datas: 1980-01-01 .. 2107-12-31 23:59:58 (resolução de 2 s).
    private static readonly DateTime ZipMinDate = new(1980, 1, 1);
    private static readonly DateTime ZipMaxDate = new(2107, 12, 31, 23, 59, 58);

    /// <summary>Arquivos com data fora do intervalo do ZIP (ex.: relógio adiantado) não podem derrubar a compactação.</summary>
    internal static DateTime ClampTimestamp(DateTime value) =>
        value < ZipMinDate ? ZipMinDate : value > ZipMaxDate ? ZipMaxDate : value;

    public static async Task CompressAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationZipPath,
        IProgress<ProgressReport>? progress,
        CancellationToken cancellationToken = default)
    {
        if (sourcePaths.Count == 0) return;

        var fileEntries = new List<(string FullPath, string RelativePath, long Length)>();
        var emptyDirs = new List<string>();
        long totalBytes = 0;

        foreach (var path in sourcePaths)
        {
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                fileEntries.Add((fi.FullName, fi.Name, fi.Length));
                totalBytes += fi.Length;
            }
            else if (Directory.Exists(path))
            {
                var di = new DirectoryInfo(path);
                string rootName = di.Name;
                bool any = false;

                foreach (var file in di.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    any = true;
                    string relPath = Path.Combine(rootName, Path.GetRelativePath(di.FullName, file.FullName));
                    fileEntries.Add((file.FullName, relPath, file.Length));
                    totalBytes += file.Length;
                }

                // Subpastas vazias (e a própria pasta, se estiver vazia) viram entradas de diretório.
                foreach (var sub in di.EnumerateDirectories("*", SearchOption.AllDirectories))
                    if (!sub.EnumerateFileSystemInfos().GetEnumerator().MoveNext())
                        emptyDirs.Add(Path.Combine(rootName, Path.GetRelativePath(di.FullName, sub.FullName)));

                if (!any && !di.EnumerateDirectories().GetEnumerator().MoveNext())
                    emptyDirs.Add(rootName);
            }
        }

        if (fileEntries.Count == 0 && emptyDirs.Count == 0) return;

        if (File.Exists(destinationZipPath))
            File.Delete(destinationZipPath);

        bool completed = false;
        try
        {
            long processedBytes = 0;

            await using (var zipStream = new FileStream(destinationZipPath, FileMode.Create, FileAccess.Write,
                             FileShare.None, BufferSize, useAsync: true))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                byte[] buffer = new byte[BufferSize];

                foreach (string dir in emptyDirs)
                    archive.CreateEntry(dir.Replace('\\', '/').TrimEnd('/') + "/");

                for (int i = 0; i < fileEntries.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var (fullPath, relPath, _) = fileEntries[i];

                    string currentStatus = Loc.Format("Compression.Compressing", i + 1, fileEntries.Count, Path.GetFileName(fullPath));
                    double percent = totalBytes > 0 ? (double)processedBytes / totalBytes * 100.0 : 0.0;
                    progress?.Report(new ProgressReport(percent, currentStatus));

                    var entry = archive.CreateEntry(relPath.Replace('\\', '/'), CompressionLevel.Optimal);

                    entry.LastWriteTime = ClampTimestamp(File.GetLastWriteTime(fullPath));

                    await using var entryStream = entry.Open();
                    await using var sourceStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true);

                    int bytesRead;
                    while ((bytesRead = await sourceStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        await entryStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                        processedBytes += bytesRead;

                        percent = totalBytes > 0 ? Math.Min(100.0, (double)processedBytes / totalBytes * 100.0) : 0.0;
                        progress?.Report(new ProgressReport(percent, currentStatus));
                    }
                }
            }

            completed = true;
            progress?.Report(new ProgressReport(100.0, Loc.Get("Compression.CompressDone")));
        }
        finally
        {
            // Cancelou ou falhou: não deixa um .zip pela metade.
            if (!completed)
            {
                try { File.Delete(destinationZipPath); } catch { /* arquivo em uso: ignora */ }
            }
        }
    }

    /// <summary>
    /// Extrai o zip para a pasta de destino. Qualquer entrada que tente sair da
    /// pasta (caminho com "..", caminho absoluto, unidade, stream alternativo)
    /// aborta a extração com InvalidDataException.
    /// </summary>
    public static async Task DecompressAsync(
        string sourceZipPath,
        string destinationDirectory,
        IProgress<ProgressReport>? progress,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceZipPath)) return;

        // Raiz canônica terminada em separador: "C:\dest\" não casa com "C:\dest-evil\".
        string destRoot = Path.GetFullPath(destinationDirectory);
        if (!destRoot.EndsWith(Path.DirectorySeparatorChar))
            destRoot += Path.DirectorySeparatorChar;

        Directory.CreateDirectory(destRoot);

        using var archive = ZipFile.OpenRead(sourceZipPath);
        int totalEntries = archive.Entries.Count;
        if (totalEntries == 0) return;

        byte[] buffer = new byte[BufferSize];

        for (int i = 0; i < totalEntries; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = archive.Entries[i];
            double percent = (double)i / totalEntries * 100.0;
            progress?.Report(new ProgressReport(percent, Loc.Format("Compression.Extracting", i + 1, totalEntries, entry.Name)));

            string targetPath = ResolveSafeTarget(destRoot, entry.FullName);

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(targetPath);   // entrada de diretório
                continue;
            }

            string? targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

            await using var entryStream = entry.Open();
            await using var targetStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write,
                FileShare.None, BufferSize, useAsync: true);

            int bytesRead;
            while ((bytesRead = await entryStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await targetStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            }
        }

        progress?.Report(new ProgressReport(100.0, Loc.Get("Compression.ExtractDone")));
    }

    private static string ResolveSafeTarget(string destRoot, string entryFullName)
    {
        // ':' bloqueia unidade ("C:\...") e streams alternativos do NTFS ("a.txt:x").
        if (entryFullName.Contains(':'))
            throw new InvalidDataException(Loc.Format("Compression.UnsafeEntryChar", entryFullName));

        string combined = Path.GetFullPath(Path.Combine(destRoot, entryFullName));

        if (!combined.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(Loc.Format("Compression.UnsafeEntryEscape", entryFullName));

        return combined;
    }
}
