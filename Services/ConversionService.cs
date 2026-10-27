// ============================================================================
// RadialFlow - Serviço de conversão
// ----------------------------------------------------------------------------
// Fachada usada pela roda e pela janela de conversão:
//   * GetAvailableOptions: formatos que FUNCIONAM para o arquivo neste PC;
//   * ConvertAsync: executa a conversão escolhida, com progresso.
// O que oferecer e quem converte vem do ConversionFormats (catálogo puro);
// o que está instalado vem do ConversionTools.
//
// Segurança: ferramentas externas NUNCA rodam como administrador (passam pelo
// UnelevatedLauncher, que valida o executável no TrustedPath antes de criar o
// processo). Saída parcial é apagada se a conversão falhar ou for cancelada.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RadialFlow.Infrastructure;
using RadialFlow.Security;

namespace RadialFlow.Services;

public static class ConversionService
{
    public static FileCategory DetectCategory(string filePath) =>
        ConversionFormats.DetectCategory(Path.GetExtension(filePath), WicCodecs.Readable);

    /// <summary>Formatos de destino que funcionam para este arquivo com o que está instalado (pode ser vazio).</summary>
    public static List<ConversionOption> GetAvailableOptions(string filePath) =>
        ConversionFormats.GetOptions(Path.GetExtension(filePath), ConversionTools.Current());

    // ------------------------------------------------------------------
    // Conversão
    // ------------------------------------------------------------------

    public static async Task ConvertAsync(
        string sourcePath,
        string targetFormat,
        string destinationPath,
        IProgress<ProgressReport>? progress,
        CancellationToken cancellationToken = default)
    {
        // Caminhos canônicos e absolutos: também impede que um nome começando com "-" vire opção da ferramenta.
        if (!TrustedPath.TryGetSafeFullPath(sourcePath, out string safeSource) || !File.Exists(safeSource))
            throw new FileNotFoundException(Loc.Get("ConversionService.SourceInvalid"), sourcePath);
        if (!TrustedPath.TryGetSafeFullPath(destinationPath, out string safeDestination))
            throw new ArgumentException(Loc.Format("ConversionService.DestinationInvalid", destinationPath), nameof(destinationPath));

        sourcePath = safeSource;
        destinationPath = safeDestination;

        string srcExt = Path.GetExtension(sourcePath).ToLowerInvariant();
        string target = ConversionFormats.NormalizeFormat(targetFormat);
        ConversionToolset tools = ConversionTools.Current();
        FileCategory category = ConversionFormats.DetectCategory(srcExt, tools.WicReadable);
        ConversionEngine engine = ConversionFormats.Plan(srcExt, target, tools);

        if (engine == ConversionEngine.None)
            throw new NotSupportedException(DescribeUnavailable(srcExt, target, category));

        await RunWithPartialOutputCleanupAsync(destinationPath, async () =>
        {
            progress?.Report(new ProgressReport(5.0, Loc.Format("ConversionService.ConvertingFormat", Path.GetFileName(sourcePath), target)));

            switch (engine)
            {
                case ConversionEngine.Wic:
                    progress?.Report(new ProgressReport(40.0, Loc.Get("ConversionService.ConvertingImage")));
                    await Task.Run(() => ImageConverter.Convert(sourcePath, target, destinationPath), cancellationToken).ConfigureAwait(false);
                    break;

                case ConversionEngine.Magick:
                    await RunProcessAsync(RequireTool(ToolResolver.GetImageMagick(), "magick.exe"),
                        ConversionFormats.BuildMagickArguments(sourcePath, target, destinationPath), progress, cancellationToken).ConfigureAwait(false);
                    break;

                case ConversionEngine.FFmpeg when category == FileCategory.Image:
                    await RunProcessAsync(RequireTool(ToolResolver.GetFFmpeg(), "ffmpeg.exe"),
                        ConversionFormats.BuildFfmpegImageArguments(sourcePath, target, destinationPath), progress, cancellationToken).ConfigureAwait(false);
                    break;

                case ConversionEngine.FFmpeg:
                    await ConvertMediaAsync(sourcePath, target, category == FileCategory.Video, destinationPath, progress, cancellationToken).ConfigureAwait(false);
                    return;   // o progresso do ffmpeg já informa a conclusão

                case ConversionEngine.Pandoc:
                    await RunPandocAsync(sourcePath, srcExt, target, destinationPath, progress, cancellationToken).ConfigureAwait(false);
                    break;

                case ConversionEngine.LibreOffice:
                    await ConvertWithLibreOfficeAsync(sourcePath, srcExt, target, destinationPath, progress, cancellationToken).ConfigureAwait(false);
                    break;

                case ConversionEngine.PandocThenLibreOffice:
                    await ConvertViaDocxAsync(sourcePath, srcExt, destinationPath, progress, cancellationToken).ConfigureAwait(false);
                    break;
            }

            progress?.Report(new ProgressReport(100.0, Loc.Get("ConversionService.Done")));
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Converte os arquivos em sequência, um de cada vez. Uma falha (formato, ferramenta, arquivo preso)
    /// é registrada e a fila continua; cancelamento interrompe tudo. O progresso de cada arquivo é
    /// escalonado para a barra única (0–100% do lote).
    /// </summary>
    internal static async Task<ConversionBatchResult> ConvertBatchAsync(
        IReadOnlyList<ConversionJob> jobs,
        string targetFormat,
        IProgress<ProgressReport>? progress,
        CancellationToken cancellationToken = default)
    {
        var items = new List<ConversionBatchItem>(jobs.Count);
        int total = jobs.Count;
        if (total == 0)
        {
            progress?.Report(new ProgressReport(100.0, Loc.Get("ConversionService.NothingToConvert")));
            return new ConversionBatchResult(items);
        }

        double span = 100.0 / total;

        for (int i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ConversionJob job = jobs[i];
            string name = Path.GetFileName(job.Source);
            IProgress<ProgressReport>? itemProgress = progress is null
                ? null
                : new BatchProgress(progress, span * i, span, i + 1, total);

            try
            {
                await ConvertAsync(job.Source, targetFormat, job.Destination, itemProgress, cancellationToken)
                    .ConfigureAwait(false);
                items.Add(new ConversionBatchItem(job.Source, job.Destination, true, null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                items.Add(new ConversionBatchItem(job.Source, job.Destination, false, ex.Message));
                progress?.Report(new ProgressReport(span * (i + 1), Loc.Format("ConversionService.ItemFailed", i + 1, total, name)));
            }
        }

        int succeeded = items.Count(item => item.Success);
        int failed = total - succeeded;
        string summary = failed == 0
            ? Loc.Format("ConversionService.BatchDone", succeeded)
            : Loc.Format("ConversionService.BatchSummary", succeeded, total, failed);
        progress?.Report(new ProgressReport(100.0, summary));

        return new ConversionBatchResult(items);
    }

    /// <summary>Mapeia 0–100% de um arquivo para a faixa dele na barra do lote e identifica a posição.</summary>
    private sealed class BatchProgress : IProgress<ProgressReport>
    {
        private readonly IProgress<ProgressReport> _inner;
        private readonly double _start;
        private readonly double _span;
        private readonly int _index;
        private readonly int _total;

        public BatchProgress(IProgress<ProgressReport> inner, double start, double span, int index, int total)
        {
            _inner = inner;
            _start = start;
            _span = span;
            _index = index;
            _total = total;
        }

        public void Report(ProgressReport value)
        {
            double local = Math.Clamp(value.Percentage, 0.0, 100.0);
            double overall = _start + local * _span / 100.0;
            _inner.Report(new ProgressReport(overall, $"({_index}/{_total}) {value.StatusText}"));
        }
    }

    private static string RequireTool(string? path, string toolName) =>
        path ?? throw new FileNotFoundException(ToolResolver.DescribeMissing(toolName));

    /// <summary>Mensagem quando não há como converter: diz qual programa resolveria.</summary>
    internal static string DescribeUnavailable(string srcExt, string target, FileCategory category)
    {
        if (srcExt == ".pdf") return Loc.Get("ConversionService.PdfUnsupported");

        string tool = category switch
        {
            FileCategory.Image => "magick.exe",
            FileCategory.Video or FileCategory.Audio => "ffmpeg.exe",
            FileCategory.Document when srcExt is ".md" or ".markdown" or ".epub" || target is "md" or "epub" => "pandoc.exe",
            FileCategory.Document or FileCategory.Spreadsheet or FileCategory.Presentation => "soffice.exe",
            _ => ""
        };

        string what = Loc.Format("ConversionService.Unavailable", srcExt, target);
        return tool.Length == 0 ? what : $"{what} {ToolResolver.DescribeMissing(tool)}";
    }

    /// <summary>
    /// Executa <paramref name="work"/>; se falhar ou for cancelado, apaga a saída parcial em
    /// <paramref name="destinationPath"/> (nunca um arquivo que já existia antes da conversão).
    /// Quando o trabalho é uma ferramenta externa, o launcher só devolve o controle com o processo
    /// (e os filhos dele) já encerrado: o arquivo não está mais preso e a exclusão funciona.
    /// </summary>
    internal static async Task RunWithPartialOutputCleanupAsync(string destinationPath, Func<Task> work)
    {
        bool existedBefore = File.Exists(destinationPath);
        bool finished = false;

        try
        {
            await work().ConfigureAwait(false);
            finished = true;
        }
        finally
        {
            if (!finished && !existedBefore)
            {
                try { if (File.Exists(destinationPath)) File.Delete(destinationPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* ainda em uso: nada a fazer */ }
            }
        }
    }

    // ---- Áudio / vídeo ----------------------------------------------

    private static async Task ConvertMediaAsync(string source, string target, bool fromVideo, string destination,
        IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        string ffmpeg = RequireTool(ToolResolver.GetFFmpeg(), "ffmpeg.exe");

        // Na conversão, os argumentos precisam ser exatos: espera a lista de codificadores (se ainda não chegou).
        IReadOnlySet<string>? encoders = await FfmpegEncoders.GetAsync(ffmpeg).WaitAsync(ct).ConfigureAwait(false);
        var tools = new ConversionToolset { HasFFmpeg = true, FfmpegEncoders = encoders };

        List<string> args = ConversionFormats.BuildFfmpegMediaArguments(source, target, fromVideo, tools, destination)
            ?? throw new NotSupportedException(Loc.Format("ConversionService.EncoderMissing", target));

        await RunFfmpegWithProgressAsync(ffmpeg, args, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executa o ffmpeg SEM elevação e converte as linhas de stderr em progresso
    /// ("Duration:" dá o total; "time=" dá a posição). O ffmpeg escreve o progresso com "\r": o
    /// leitor de linhas do launcher trata "\r" como fim de linha, então cada atualização chega separada.
    /// </summary>
    internal static async Task RunFfmpegWithProgressAsync(string ffmpegPath, IReadOnlyList<string> args,
        IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        TimeSpan totalDuration = TimeSpan.Zero;   // só a thread de leitura do stderr toca nesta variável

        void OnErrorLine(string line)
        {
            if (!TryParseFfmpegProgress(line, ref totalDuration, out TimeSpan current, out double percentage)) return;

            progress?.Report(new ProgressReport(percentage,
                Loc.Format("ConversionService.Processing", $"{current:hh\\:mm\\:ss} / {totalDuration:hh\\:mm\\:ss} ({percentage:0}%)")));
        }
        ToolResult result = await RunToolAsync(ffmpegPath, args, workingDirectory: null, OnErrorLine, ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw new InvalidOperationException(Loc.Format("ConversionService.FfmpegExit", result.ExitCode, Truncate(result.ErrorTail, 600)));

        progress?.Report(new ProgressReport(100.0, Loc.Get("ConversionService.MediaDone")));
    }

    /// <summary>
    /// Interpreta uma linha do stderr do ffmpeg. "Duration: hh:mm:ss.xx" fixa o total (uma vez);
    /// "time=hh:mm:ss.xx" informa a posição atual.
    /// </summary>
    /// <param name="totalDuration">Total conhecido; atualizado quando a linha traz "Duration:".</param>
    /// <returns>true se a linha tinha a posição atual e há total para calcular o percentual (máximo 99%).</returns>
    internal static bool TryParseFfmpegProgress(string line, ref TimeSpan totalDuration, out TimeSpan current, out double percentage)
    {
        current = default;
        percentage = 0;

        if (totalDuration == TimeSpan.Zero && TryParseClock(DurationRegex.Match(line), out var total))
            totalDuration = total;

        if (totalDuration <= TimeSpan.Zero || !TryParseClock(TimeRegex.Match(line), out current)) return false;

        percentage = Math.Min(99.0, current.TotalSeconds / totalDuration.TotalSeconds * 100.0);
        return true;
    }
    private static readonly Regex DurationRegex = new(@"Duration:\s*(\d{2}):(\d{2}):(\d{2}\.\d+)", RegexOptions.Compiled);
    private static readonly Regex TimeRegex = new(@"time=(\d{2}):(\d{2}):(\d{2}\.\d+)", RegexOptions.Compiled);

    private static bool TryParseClock(Match m, out TimeSpan value)
    {
        value = default;
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups[1].Value, out int h) || !int.TryParse(m.Groups[2].Value, out int min) ||
            !double.TryParse(m.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double s))
            return false;
        value = TimeSpan.FromHours(h) + TimeSpan.FromMinutes(min) + TimeSpan.FromSeconds(s);
        return true;
    }

    // ---- Documentos --------------------------------------------------

    /// <summary>Pandoc na pasta do arquivo: imagens com caminho relativo (comuns em Markdown) são encontradas.</summary>
    private static Task RunPandocAsync(string source, string srcExt, string target, string destination,
        IProgress<ProgressReport>? progress, CancellationToken ct) =>
        RunProcessAsync(RequireTool(ToolResolver.GetPandoc(), "pandoc.exe"),
            ConversionFormats.BuildPandocArguments(source, srcExt, target, destination), progress, ct,
            workingDirectory: Path.GetDirectoryName(source));

    /// <summary>Markdown/EPUB para PDF sem LaTeX: o Pandoc gera um DOCX temporário e o LibreOffice o transforma em PDF.</summary>
    private static async Task ConvertViaDocxAsync(string source, string srcExt, string destination,
        IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        string work = CreateWorkDirectory();
        try
        {
            string docx = Path.Combine(work, Path.GetFileNameWithoutExtension(source) + ".docx");
            await RunPandocAsync(source, srcExt, "docx", docx, progress, ct).ConfigureAwait(false);
            progress?.Report(new ProgressReport(60.0, Loc.Get("ConversionService.GeneratingPdf")));
            await ConvertWithLibreOfficeAsync(docx, ".docx", "pdf", destination, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            DeleteWorkDirectory(work);
        }
    }

    /// <summary>
    /// O LibreOffice grava "nome.ext" na pasta de saída, que nem sempre é o destino escolhido. Converte numa
    /// pasta temporária (com perfil isolado, para não colidir com uma instância aberta) e move o resultado.
    /// </summary>
    private static async Task ConvertWithLibreOfficeAsync(string source, string srcExt, string target, string destination,
        IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        string soffice = RequireTool(ToolResolver.GetLibreOffice(), "soffice.exe");
        string work = CreateWorkDirectory();
        string outDir = Path.Combine(work, "out");
        string profile = Path.Combine(work, "profile");
        Directory.CreateDirectory(outDir);

        try
        {
            char separator = srcExt == ".csv" ? ConversionFormats.DetectCsvSeparator(ReadFirstLine(source)) : ',';
            List<string> args = ConversionFormats.BuildLibreOfficeArguments(source, srcExt, target, new Uri(profile).AbsoluteUri, outDir, separator);

            await RunProcessAsync(soffice, args, progress, ct, workingDirectory: work).ConfigureAwait(false);

            string? produced = Directory.EnumerateFiles(outDir).FirstOrDefault()
                ?? throw new InvalidOperationException(Loc.Get("ConversionService.LibreOfficeNoFile"));

            File.Move(produced, destination, overwrite: false);
        }
        finally
        {
            DeleteWorkDirectory(work);
        }
    }

    private static string? ReadFirstLine(string path)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadLine();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string CreateWorkDirectory()
    {
        string work = Path.Combine(Path.GetTempPath(), "RadialFlow", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        return work;
    }

    /// <summary>Idade mínima para considerar uma pasta abandonada; uma conversão dura minutos, não um dia.</summary>
    private static readonly TimeSpan StaleWorkDirectoryAge = TimeSpan.FromDays(1);

    /// <summary>
    /// Remove pastas deixadas por conversões anteriores. A exclusão imediata é best-effort (a ferramenta pode
    /// ainda estar segurando o arquivo), então a inicialização remove as que já estão velhas. Só considera
    /// filhos com nome de GUID "N" — o formato criado por <see cref="CreateWorkDirectory"/> — e ignora links.
    /// </summary>
    internal static int CleanUpStaleWorkDirectories()
    {
        string root = Path.Combine(Path.GetTempPath(), "RadialFlow");
        return CleanUpStaleWorkDirectories(root, DateTime.UtcNow - StaleWorkDirectoryAge);
    }

    internal static int CleanUpStaleWorkDirectories(string root, DateTime cutoffUtc)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(root) || IsReparsePoint(root)) return 0;

            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                try
                {
                    if (!Guid.TryParseExact(Path.GetFileName(dir), "N", out _)) continue;
                    if (IsReparsePoint(dir)) continue;
                    if (Directory.GetLastWriteTimeUtc(dir) >= cutoffUtc) continue;

                    Directory.Delete(dir, recursive: true);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return removed;
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void DeleteWorkDirectory(string work)
    {
        try { Directory.Delete(work, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* fica para a limpeza de inicialização */ }
    }

    // ---- Processos externos -----------------------------------------

    /// <summary>Código de saída e o final do stderr (para mensagens de erro).</summary>
    internal readonly record struct ToolResult(int ExitCode, string ErrorTail);

    /// <summary>
    /// Executa uma ferramenta externa pelo <see cref="UnelevatedLauncher"/>: NUNCA como administrador.
    /// O launcher valida o executável em <see cref="TrustedPath.IsTrusted"/> antes de criar qualquer
    /// processo, drena stdout/stderr (um pipe cheio travaria a ferramenta) e, ao cancelar, encerra o
    /// processo e os filhos dele antes de lançar <see cref="OperationCanceledException"/> — por isso a saída
    /// parcial pode ser apagada logo em seguida sem arquivo preso.
    /// </summary>
    /// <param name="onErrorLine">Cada linha de stderr (thread de leitura; não toca na UI direto).</param>
    /// <exception cref="UnelevatedLaunchException">Executável recusado ou impossível iniciar sem elevação.</exception>
    internal static async Task<ToolResult> RunToolAsync(string executable, IReadOnlyList<string> args,
        string? workingDirectory, Action<string>? onErrorLine, CancellationToken ct)
    {
        var tail = new StringBuilder();

        void OnErrorLine(string line)
        {
            if (line.Length > 0)
            {
                lock (tail)
                {
                    tail.AppendLine(line);
                    if (tail.Length > 4096) tail.Remove(0, tail.Length - 4096);   // guarda só o final
                }
            }

            onErrorLine?.Invoke(line);
        }

        int exitCode = await UnelevatedLauncher.ExecuteAsync(executable,
            new UnelevatedProcessOptions
            {
                Arguments = args,
                WorkingDirectory = workingDirectory,
                OnErrorLine = OnErrorLine   // stdout não tem callback: é lido e descartado
            }, ct).ConfigureAwait(false);

        string errorTail;
        lock (tail) errorTail = tail.ToString().Trim();
        return new ToolResult(exitCode, errorTail);
    }

    /// <summary>
    /// Limite para documentos e imagens (Pandoc, LibreOffice, ImageMagick): um arquivo estranho pode travar a ferramenta
    /// em vez de falhar. Não vale para o FFmpeg, em que um vídeo longo demora mesmo (e o progresso mostra que anda).
    /// </summary>
    internal static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Executa uma ferramenta e falha com o resumo do stderr se o código de saída não for 0.</summary>
    /// <param name="timeout">Limite de tempo; padrão <see cref="ToolTimeout"/>.</param>
    internal static async Task RunProcessAsync(string executable, IReadOnlyList<string> args,
        IProgress<ProgressReport>? progress, CancellationToken ct, string? workingDirectory = null, TimeSpan? timeout = null)
    {
        progress?.Report(new ProgressReport(40.0, Loc.Format("ConversionService.ConvertingWith", Path.GetFileNameWithoutExtension(executable))));

        TimeSpan limitAfter = timeout ?? ToolTimeout;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(limitAfter);

        ToolResult result;
        try
        {
            result = await RunToolAsync(executable, args, workingDirectory, onErrorLine: null, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // O launcher já encerrou a ferramenta e os processos filhos dela.
            string time = limitAfter.TotalMinutes >= 1
                ? Loc.Format("ConversionService.TimeoutMinutes", limitAfter.TotalMinutes)
                : Loc.Format("ConversionService.TimeoutSeconds", limitAfter.TotalSeconds);
            throw new TimeoutException(
                Loc.Format("ConversionService.Timeout", Path.GetFileNameWithoutExtension(executable), time));
        }

        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                Loc.Format("ConversionService.ToolFailed", Path.GetFileName(executable), result.ExitCode, Truncate(result.ErrorTail, 600)));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : "…" + s[^max..];
}
