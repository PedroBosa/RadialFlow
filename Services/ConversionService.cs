// ============================================================================
// RadialFlow - Serviço de Conversão Multi-Formato
// ----------------------------------------------------------------------------
// Motores:
//   WIC (nativo)        imagens PNG/JPG/BMP/TIFF/GIF, sem instalar nada
//   ImageMagick         WEBP, ICO, AVIF, SVG, HEIC...
//   FFmpeg              áudio, vídeo e GIF animado (progresso em tempo real)
//   Pandoc / LibreOffice documentos
//
// Correções desta versão:
//   * as opções oferecidas dependem das ferramentas REALMENTE disponíveis;
//   * FLAC/OGG/AAC usam o codec certo (antes geravam comando inválido);
//   * processos externos NÃO rodam como administrador: passam pelo
//     UnelevatedLauncher.ExecuteAsync (token do shell, TrustedPath antes do launch,
//     stdout/stderr drenados em tempo real, Job Object para cancelar a árvore);
//   * documentos: motor escolhido por origem/destino (PDF e Office via
//     LibreOffice; texto/markup via Pandoc);
//   * PNG com transparência -> JPEG/BMP é composto sobre fundo branco;
//   * saída parcial é apagada se a conversão falhar ou for cancelada.
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
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RadialFlow.Security;

namespace RadialFlow.Services;

public enum FileCategory
{
    Image,
    Video,
    Audio,
    Document,
    Unsupported
}

public sealed record ConversionOption(string TargetFormat, string DisplayName, string Engine);

public static class ConversionService
{
    private enum Engine { None, Wic, Magick, FFmpeg, Pandoc, LibreOffice }

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".ico", ".webp", ".avif", ".heic", ".svg"
    };

    // Formatos que o WIC decodifica em qualquer Windows (webp/heic/avif/svg dependem de extensões ou não existem).
    private static readonly HashSet<string> WicSourceExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".ico"
    };

    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v"
    };

    private static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".aac", ".flac", ".ogg", ".m4a", ".wma"
    };

    private static readonly HashSet<string> DocExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".docx", ".pdf", ".html", ".htm", ".epub", ".txt", ".rtf", ".odt", ".pptx", ".xlsx"
    };

    public static FileCategory DetectCategory(string filePath)
    {
        string ext = Path.GetExtension(filePath);
        if (ImageExts.Contains(ext)) return FileCategory.Image;
        if (VideoExts.Contains(ext)) return FileCategory.Video;
        if (AudioExts.Contains(ext)) return FileCategory.Audio;
        if (DocExts.Contains(ext)) return FileCategory.Document;
        return FileCategory.Unsupported;
    }

    // ------------------------------------------------------------------
    // Opções oferecidas (somente o que realmente funciona neste PC)
    // ------------------------------------------------------------------

    public static List<ConversionOption> GetAvailableOptions(string filePath)
    {
        var category = DetectCategory(filePath);
        string srcExt = Path.GetExtension(filePath).ToLowerInvariant();
        var options = new List<ConversionOption>();

        void Add(string target, string display)
        {
            if (SameFormat(srcExt, target)) return;

            Engine engine = category switch
            {
                FileCategory.Image => ImageEngine(srcExt, target),
                FileCategory.Video or FileCategory.Audio => ToolResolver.GetFFmpeg() is null ? Engine.None : Engine.FFmpeg,
                FileCategory.Document => DocEngine(srcExt, target),
                _ => Engine.None
            };

            if (engine != Engine.None)
                options.Add(new ConversionOption(target, display, EngineName(engine)));
        }

        switch (category)
        {
            case FileCategory.Image:
                Add("png",  "PNG — Imagem sem perdas");
                Add("jpg",  "JPEG — Imagem comprimida");
                Add("webp", "WEBP — Alta compressão Web");
                Add("bmp",  "BMP — Bitmap padrão");
                Add("tiff", "TIFF — Imagem de alta fidelidade");
                Add("ico",  "ICO — Ícone do Windows");
                break;

            case FileCategory.Video:
                Add("mp4",  "MP4 — H.264 / AAC padrão universal");
                Add("gif",  "GIF Animado — Clipe animado");
                Add("webm", "WebM — Vídeo otimizado para web");
                Add("mp3",  "MP3 — Extrair somente áudio");
                Add("wav",  "WAV — Extrair áudio sem perdas");
                break;

            case FileCategory.Audio:
                Add("mp3",  "MP3 — Áudio compatível universal");
                Add("wav",  "WAV — Áudio sem compressão");
                Add("aac",  "AAC — Áudio de alta eficiência");
                Add("flac", "FLAC — Áudio sem perdas (Lossless)");
                Add("ogg",  "OGG — Áudio Vorbis");
                break;

            case FileCategory.Document:
                Add("pdf",  "PDF — Documento portátil");
                Add("docx", "DOCX — Microsoft Word");
                Add("html", "HTML — Página Web");
                Add("md",   "Markdown — Texto formatado");
                Add("txt",  "TXT — Texto puro");
                break;
        }

        return options;
    }

    private static bool SameFormat(string srcExt, string target)
    {
        static string Norm(string e) => e.TrimStart('.').ToLowerInvariant() switch
        {
            "jpeg" => "jpg",
            "tif" => "tiff",
            "htm" => "html",
            "markdown" => "md",
            var x => x
        };
        return Norm(srcExt) == Norm(target);
    }

    private static string EngineName(Engine e) => e switch
    {
        Engine.Wic => "WIC",
        Engine.Magick => "ImageMagick",
        Engine.FFmpeg => "FFmpeg",
        Engine.Pandoc => "Pandoc",
        Engine.LibreOffice => "LibreOffice",
        _ => ""
    };

    // ------------------------------------------------------------------
    // Escolha de motor (a MESMA função alimenta as opções e a conversão)
    // ------------------------------------------------------------------

    private static Engine ImageEngine(string srcExt, string target)
    {
        bool wicSource = WicSourceExts.Contains(srcExt);
        bool wicTarget = target is "png" or "jpg" or "jpeg" or "bmp" or "tiff" or "tif" or "gif";

        if (wicSource && wicTarget) return Engine.Wic;
        if (ToolResolver.GetImageMagick() is not null) return Engine.Magick;

        // Sem ImageMagick, o ffmpeg decodifica webp/avif e grava formatos comuns.
        bool ffmpegCanRead = srcExt is not ".svg" and not ".heic";
        bool ffmpegCanWrite = target is "png" or "jpg" or "jpeg" or "webp" or "bmp" or "tiff";
        if (ffmpegCanRead && ffmpegCanWrite && ToolResolver.GetFFmpeg() is not null) return Engine.FFmpeg;

        return Engine.None;
    }

    private static Engine DocEngine(string srcExt, string target)
    {
        bool hasPandoc = ToolResolver.GetPandoc() is not null;
        bool hasOffice = ToolResolver.GetLibreOffice() is not null;

        if (srcExt == ".pdf") return Engine.None;   // nenhum motor aqui lê PDF com fidelidade

        if (srcExt is ".pptx" or ".xlsx")
            return target == "pdf" && hasOffice ? Engine.LibreOffice : Engine.None;

        bool pandocReads = srcExt is ".md" or ".markdown" or ".docx" or ".odt" or ".html" or ".htm" or ".epub" or ".txt" or ".rtf";
        bool officeReads = srcExt is ".docx" or ".odt" or ".rtf" or ".html" or ".htm" or ".txt";

        if (target == "pdf")
        {
            if (officeReads && hasOffice) return Engine.LibreOffice;          // sem LaTeX
            if (pandocReads && hasPandoc) return Engine.Pandoc;               // exige motor PDF instalado
            return Engine.None;
        }

        if (pandocReads && hasPandoc) return Engine.Pandoc;
        if (officeReads && hasOffice && target is "docx" or "html" or "txt") return Engine.LibreOffice;
        return Engine.None;
    }

    private static string MissingToolMessage(string tool) => ToolResolver.DescribeMissing(tool);

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
            throw new FileNotFoundException("Arquivo de origem inválido ou inexistente.", sourcePath);
        if (!TrustedPath.TryGetSafeFullPath(destinationPath, out string safeDestination))
            throw new ArgumentException($"Caminho de destino inválido: '{destinationPath}'.", nameof(destinationPath));

        sourcePath = safeSource;
        destinationPath = safeDestination;

        var category = DetectCategory(sourcePath);
        string srcExt = Path.GetExtension(sourcePath).ToLowerInvariant();
        string target = targetFormat.TrimStart('.').ToLowerInvariant();

        await RunWithPartialOutputCleanupAsync(destinationPath, async () =>
        {
            progress?.Report(new ProgressReport(5.0, $"Iniciando conversão de {Path.GetFileName(sourcePath)} para .{target}..."));

            switch (category)
            {
                case FileCategory.Image:
                    await ConvertImageAsync(sourcePath, srcExt, target, destinationPath, progress, cancellationToken).ConfigureAwait(false);
                    break;

                case FileCategory.Video:
                case FileCategory.Audio:
                    await ConvertMediaAsync(sourcePath, target, destinationPath, progress, cancellationToken).ConfigureAwait(false);
                    break;

                case FileCategory.Document:
                    await ConvertDocumentAsync(sourcePath, srcExt, target, destinationPath, progress, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    throw new NotSupportedException($"Formato .{target} não suportado para o arquivo selecionado.");
            }
        }).ConfigureAwait(false);
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

    // ---- Imagens -----------------------------------------------------

    private static async Task ConvertImageAsync(string source, string srcExt, string target, string destination,
        IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        switch (ImageEngine(srcExt, target))
        {
            case Engine.Wic:
                await Task.Run(() => ConvertImageWic(source, target, destination), ct).ConfigureAwait(false);
                progress?.Report(new ProgressReport(100.0, "Conversão de imagem concluída com sucesso!"));
                break;

            case Engine.Magick:
            {
                string magick = ToolResolver.GetImageMagick()!;
                var args = new List<string> { source };
                if (target == "ico")
                {
                    args.Add("-define");
                    args.Add("icon:auto-resize=256,128,64,48,32,16");
                }
                args.Add(destination);

                await RunProcessAsync(magick, args, progress, ct).ConfigureAwait(false);
                progress?.Report(new ProgressReport(100.0, "Conversão concluída via ImageMagick!"));
                break;
            }

            case Engine.FFmpeg:
            {
                string ffmpeg = ToolResolver.GetFFmpeg()!;
                await RunProcessAsync(ffmpeg,
                    new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", source, destination },
                    progress, ct).ConfigureAwait(false);
                progress?.Report(new ProgressReport(100.0, "Conversão concluída via FFmpeg!"));
                break;
            }

            default:
                throw new NotSupportedException(
                    $"Para converter para .{target} a partir de {srcExt}: {MissingToolMessage("magick.exe")}");
        }
    }

    private static void ConvertImageWic(string sourcePath, string targetExt, string destinationPath)
    {
        using var inStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(inStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        if (decoder.Frames.Count == 0)
            throw new InvalidOperationException("Não foi possível decodificar o arquivo de imagem.");

        BitmapSource frame = decoder.Frames[0];

        // JPEG e BMP não têm transparência: sem isso o fundo transparente sairia preto.
        if (targetExt is "jpg" or "jpeg" or "bmp" && HasAlpha(frame))
            frame = FlattenOnWhite(frame);

        BitmapEncoder encoder = targetExt switch
        {
            "png" => new PngBitmapEncoder(),
            "jpg" or "jpeg" => new JpegBitmapEncoder { QualityLevel = 90 },
            "bmp" => new BmpBitmapEncoder(),
            "tiff" or "tif" => new TiffBitmapEncoder(),
            "gif" => new GifBitmapEncoder(),
            _ => throw new NotSupportedException($"WIC não suporta o formato {targetExt}")
        };

        encoder.Frames.Add(BitmapFrame.Create(frame));

        using var outStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(outStream);
    }

    private static bool HasAlpha(BitmapSource s) =>
        s.Format == PixelFormats.Bgra32 || s.Format == PixelFormats.Pbgra32 ||
        s.Format == PixelFormats.Rgba64 || s.Format == PixelFormats.Prgba64 ||
        s.Format == PixelFormats.Rgba128Float || s.Format == PixelFormats.Prgba128Float ||
        (s.Palette?.Colors.Any(c => c.A < 255) ?? false);

    /// <summary>Compõe a imagem sobre branco, em pixels (sem depender de renderização WPF/STA).</summary>
    private static BitmapSource FlattenOnWhite(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight, stride = width * 4;

        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            int a = pixels[i + 3];
            if (a == 255) continue;

            pixels[i]     = (byte)((pixels[i]     * a + 255 * (255 - a)) / 255);
            pixels[i + 1] = (byte)((pixels[i + 1] * a + 255 * (255 - a)) / 255);
            pixels[i + 2] = (byte)((pixels[i + 2] * a + 255 * (255 - a)) / 255);
            pixels[i + 3] = 255;
        }

        var flat = BitmapSource.Create(width, height, source.DpiX, source.DpiY, PixelFormats.Bgr32, null, pixels, stride);
        flat.Freeze();
        return flat;
    }

    // ---- Áudio / vídeo ----------------------------------------------

    private static async Task ConvertMediaAsync(string source, string target, string destination,
        IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        string? ffmpeg = ToolResolver.GetFFmpeg();
        if (string.IsNullOrEmpty(ffmpeg))
            throw new FileNotFoundException(MissingToolMessage("ffmpeg.exe"));

        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", source };

        // Cada formato com o codec que o contêiner aceita.
        switch (target)
        {
            case "gif":
                args.AddRange(new[] { "-vf", "fps=15,scale=480:-1:flags=lanczos" });
                break;
            case "mp3":
                args.AddRange(new[] { "-vn", "-ar", "44100", "-ac", "2", "-c:a", "libmp3lame", "-b:a", "192k" });
                break;
            case "wav":
                args.AddRange(new[] { "-vn", "-c:a", "pcm_s16le" });
                break;
            case "aac":
                args.AddRange(new[] { "-vn", "-c:a", "aac", "-b:a", "192k" });
                break;
            case "flac":
                args.AddRange(new[] { "-vn", "-c:a", "flac" });
                break;
            case "ogg":
                args.AddRange(new[] { "-vn", "-c:a", "libvorbis", "-q:a", "5" });
                break;
            case "webm":
                args.AddRange(new[] { "-c:v", "libvpx-vp9", "-crf", "30", "-b:v", "0", "-c:a", "libopus", "-b:a", "128k" });
                break;
            default: // mp4 e demais contêineres de vídeo
                args.AddRange(new[] { "-c:v", "libx264", "-preset", "fast", "-crf", "22", "-c:a", "aac", "-b:a", "160k" });
                break;
        }

        args.Add(destination);

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
                $"Processando: {current:hh\\:mm\\:ss} / {totalDuration:hh\\:mm\\:ss} ({percentage:0}%)"));
        }
        ToolResult result = await RunToolAsync(ffmpegPath, args, workingDirectory: null, OnErrorLine, ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg encerrou com código {result.ExitCode}.\n{Truncate(result.ErrorTail, 600)}");

        progress?.Report(new ProgressReport(100.0, "Conversão de mídia concluída com sucesso!"));
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

    private static async Task ConvertDocumentAsync(string source, string srcExt, string target, string destination,
        IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        switch (DocEngine(srcExt, target))
        {
            case Engine.Pandoc:
            {
                string pandoc = ToolResolver.GetPandoc()!;
                var args = new List<string> { "-s", source, "-o", destination };
                if (target == "txt") { args.Add("-t"); args.Add("plain"); }

                await RunProcessAsync(pandoc, args, progress, ct).ConfigureAwait(false);
                progress?.Report(new ProgressReport(100.0, "Conversão concluída via Pandoc!"));
                break;
            }

            case Engine.LibreOffice:
                await ConvertWithLibreOfficeAsync(source, target, destination, progress, ct).ConfigureAwait(false);
                progress?.Report(new ProgressReport(100.0, "Conversão concluída via LibreOffice!"));
                break;

            default:
                string missing = srcExt == ".pdf"
                    ? "Converter a partir de PDF não é suportado."
                    : $"Nenhum conversor disponível para {srcExt} -> .{target}. " +
                      MissingToolMessage(target == "pdf" ? "soffice.exe" : "pandoc.exe");
                throw new NotSupportedException(missing);
        }
    }

    /// <summary>
    /// O LibreOffice grava "nome.ext" na pasta de saída, que nem sempre é o
    /// destino escolhido. Converte numa pasta temporária (com perfil isolado,
    /// para não colidir com uma instância aberta) e move o resultado.
    /// </summary>
    private static async Task ConvertWithLibreOfficeAsync(string source, string target, string destination,
        IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        string soffice = ToolResolver.GetLibreOffice()!;
        string work = Path.Combine(Path.GetTempPath(), "RadialFlow", Guid.NewGuid().ToString("N"));
        string outDir = Path.Combine(work, "out");
        string profile = Path.Combine(work, "profile");
        Directory.CreateDirectory(outDir);

        try
        {
            string filter = target == "txt" ? "txt:Text" : target;
            var args = new List<string>
            {
                $"-env:UserInstallation={new Uri(profile).AbsoluteUri}",
                "--headless", "--convert-to", filter, "--outdir", outDir, source
            };

            await RunProcessAsync(soffice, args, progress, ct, workingDirectory: work).ConfigureAwait(false);

            string? produced = Directory.EnumerateFiles(outDir).FirstOrDefault();
            if (produced is null)
                throw new InvalidOperationException("O LibreOffice não gerou nenhum arquivo.");

            File.Move(produced, destination, overwrite: false);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* temp: ignora */ }
        }
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

    /// <summary>Executa uma ferramenta e falha com o resumo do stderr se o código de saída não for 0.</summary>
    private static async Task RunProcessAsync(string executable, IReadOnlyList<string> args,
        IProgress<ProgressReport>? progress, CancellationToken ct, string? workingDirectory = null)
    {
        progress?.Report(new ProgressReport(40.0, "Processando com ferramenta externa..."));

        ToolResult result = await RunToolAsync(executable, args, workingDirectory, onErrorLine: null, ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Erro ao executar {Path.GetFileName(executable)} (código {result.ExitCode}):\n{Truncate(result.ErrorTail, 600)}");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : "…" + s[^max..];
}