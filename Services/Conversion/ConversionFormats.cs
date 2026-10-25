// ============================================================================
// RadialFlow - Catálogo de conversões (puro, sem disco nem processos)
// ----------------------------------------------------------------------------
// Responde três perguntas, a partir da extensão de origem e das ferramentas
// disponíveis (ConversionToolset):
//   1. Quais formatos oferecer?           GetOptions
//   2. Quem faz cada conversão?           Plan
//   3. Com quais argumentos?              Build*Arguments
// A MESMA função Plan alimenta as opções mostradas e a conversão executada:
// nada é oferecido sem um caminho que funcione.
//
// Motores:
//   Windows (WIC)  imagens, sem instalar nada (WebP/HEIC/AVIF/RAW com as
//                  extensões de imagem da Microsoft Store); ICO e PDF próprios
//   ImageMagick    WEBP e formatos que o Windows não lê (SVG, JPEG XL...)
//   FFmpeg         áudio e vídeo (só codificadores que o FFmpeg instalado tem)
//   LibreOffice    documentos, planilhas e apresentações (filtros explícitos)
//   Pandoc         Markdown e EPUB; PDF via Pandoc -> DOCX -> LibreOffice
//                  (o PDF direto do Pandoc exige LaTeX e falharia)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace RadialFlow.Services;

public enum FileCategory
{
    Image,
    Video,
    Audio,
    Document,
    Spreadsheet,
    Presentation,
    Unsupported
}

public sealed record ConversionOption(string TargetFormat, string DisplayName, string Engine);

internal enum ConversionEngine { None, Wic, Magick, FFmpeg, Pandoc, LibreOffice, PandocThenLibreOffice }

/// <summary>O que está disponível numa máquina. Imutável; os testes montam à mão.</summary>
internal sealed record ConversionToolset
{
    /// <summary>Extensões (com ponto, minúsculas) que o Windows decodifica.</summary>
    public IReadOnlySet<string> WicReadable { get; init; } = ConversionFormats.BuiltInWicReadable;

    /// <summary>O codificador HEIF do Windows funciona (exige as extensões HEIF e HEVC).</summary>
    public bool CanWriteHeic { get; init; }

    public bool HasFFmpeg { get; init; }

    /// <summary>Codificadores não experimentais do FFmpeg; null = ainda não consultado (assume os comuns).</summary>
    public IReadOnlySet<string>? FfmpegEncoders { get; init; }

    public bool HasMagick { get; init; }
    public bool HasPandoc { get; init; }
    public bool HasLibreOffice { get; init; }

    public bool HasEncoder(string name) => HasFFmpeg && (FfmpegEncoders is null || FfmpegEncoders.Contains(name));
}

internal static class ConversionFormats
{
    // ------------------------------------------------------------------
    // Extensões de origem
    // ------------------------------------------------------------------

    /// <summary>Decodificadores que vêm em todo Windows 10/11.</summary>
    internal static readonly IReadOnlySet<string> BuiltInWicReadable = Set(
        ".bmp", ".dib", ".gif", ".ico", ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".tif", ".tiff", ".jxr", ".wdp");

    /// <summary>Imagens conhecidas mesmo quando nada aqui as lê (o Converter some se não houver motor).</summary>
    private static readonly HashSet<string> KnownImageExts = Set(
        ".bmp", ".dib", ".gif", ".ico", ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".tif", ".tiff", ".jxr", ".wdp",
        ".webp", ".heic", ".heif", ".hif", ".avif", ".svg", ".jxl",
        ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".orf", ".rw2", ".raf", ".pef", ".srw", ".x3f");

    /// <summary>Origens fotográficas: no PDF a imagem vai como JPEG (menor); as demais vão sem perda.</summary>
    private static readonly HashSet<string> PhotoExts = Set(
        ".jpg", ".jpeg", ".jpe", ".jfif", ".webp", ".heic", ".heif", ".hif", ".avif",
        ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".orf", ".rw2", ".raf", ".pef", ".srw", ".x3f");

    /// <summary>Imagens que o próprio FFmpeg decodifica (rota sem ImageMagick).</summary>
    private static readonly HashSet<string> FfmpegImageReadable = Set(
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".tif", ".tiff", ".gif", ".webp", ".avif");

    private static readonly HashSet<string> VideoExts = Set(
        ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".flv", ".mpg", ".mpeg", ".ts", ".mts", ".m2ts", ".3gp", ".ogv");

    private static readonly HashSet<string> AudioExts = Set(
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wma", ".aiff", ".aif", ".amr", ".ac3", ".mka");

    private static readonly HashSet<string> OfficeTextExts = Set(".docx", ".doc", ".odt", ".rtf");
    private static readonly HashSet<string> PandocTextExts = Set(".md", ".markdown", ".epub");
    private static readonly HashSet<string> HtmlExts = Set(".html", ".htm");
    private static readonly HashSet<string> SpreadsheetExts = Set(".xlsx", ".xls", ".ods", ".csv");
    private static readonly HashSet<string> PresentationExts = Set(".pptx", ".ppt", ".odp");

    // ------------------------------------------------------------------
    // Destinos, na ordem em que aparecem (a roda mostra até 8)
    // ------------------------------------------------------------------

    private static readonly string[] ImageTargets = ["png", "jpg", "pdf", "ico", "webp", "heic", "gif", "bmp", "tiff"];
    private static readonly string[] VideoTargets = ["mp4", "mkv", "webm", "mov", "gif", "mp3", "m4a", "wav"];
    private static readonly string[] AudioTargets = ["mp3", "m4a", "wav", "flac", "ogg", "opus", "aac", "aiff"];
    private static readonly string[] OfficeTextTargets = ["pdf", "docx", "odt", "rtf", "txt", "html", "md", "epub"];
    private static readonly string[] PlainTextTargets = ["pdf", "docx", "odt", "rtf", "html"];
    private static readonly string[] HtmlTargets = ["pdf", "docx", "odt", "rtf", "txt", "md", "epub"];
    private static readonly string[] MarkdownTargets = ["pdf", "docx", "html", "odt", "rtf", "epub", "txt"];
    private static readonly string[] EpubTargets = ["pdf", "docx", "html", "odt", "rtf", "md", "txt"];
    private static readonly string[] SpreadsheetTargets = ["pdf", "xlsx", "ods", "csv"];
    private static readonly string[] PresentationTargets = ["pdf", "pptx", "odp"];

    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["png"] = "PNG — imagem sem perda de qualidade",
        ["jpg"] = "JPG — foto, arquivo menor",
        ["pdf"] = "PDF — para enviar ou imprimir",
        ["ico"] = "ICO — ícone do Windows",
        ["webp"] = "WEBP — imagem leve para a web",
        ["heic"] = "HEIC — foto bem compacta (formato do iPhone)",
        ["gif"] = "GIF — imagem simples, até 256 cores",
        ["bmp"] = "BMP — imagem sem compressão",
        ["tiff"] = "TIFF — alta qualidade, para impressão",

        ["mp4"] = "MP4 — vídeo que abre em qualquer lugar",
        ["mkv"] = "MKV — vídeo, aceita várias faixas",
        ["webm"] = "WEBM — vídeo para a web",
        ["mov"] = "MOV — vídeo para Apple e QuickTime",
        ["mp3"] = "MP3 — áudio que toca em qualquer lugar",
        ["m4a"] = "M4A — áudio AAC, ótimo para celular",
        ["wav"] = "WAV — áudio sem compressão",
        ["flac"] = "FLAC — áudio sem perda, menor que WAV",
        ["ogg"] = "OGG — áudio Vorbis",
        ["opus"] = "OPUS — áudio bem compacto",
        ["aac"] = "AAC — áudio AAC puro",
        ["aiff"] = "AIFF — áudio sem compressão (Apple)",

        ["docx"] = "DOCX — Word",
        ["odt"] = "ODT — LibreOffice Writer",
        ["rtf"] = "RTF — texto formatado, abre em qualquer editor",
        ["txt"] = "TXT — só o texto, sem formatação",
        ["html"] = "HTML — página da web",
        ["md"] = "Markdown — texto com formatação simples",
        ["epub"] = "EPUB — livro digital",
        ["xlsx"] = "XLSX — Excel",
        ["ods"] = "ODS — LibreOffice Calc",
        ["csv"] = "CSV — tabela em texto (só a primeira planilha)",
        ["pptx"] = "PPTX — PowerPoint",
        ["odp"] = "ODP — LibreOffice Impress",
    };

    // ------------------------------------------------------------------
    // Normalização e categoria
    // ------------------------------------------------------------------

    /// <summary>Formato canônico sem ponto: "JPEG" → "jpg", ".tif" → "tiff", "htm" → "html"...</summary>
    internal static string NormalizeFormat(string? format) => (format ?? "").Trim().TrimStart('.').ToLowerInvariant() switch
    {
        "jpeg" or "jpe" or "jfif" => "jpg",
        "tif" => "tiff",
        "htm" => "html",
        "markdown" => "md",
        "heif" or "hif" => "heic",
        "aif" => "aiff",
        "oga" => "ogg",
        var x => x
    };

    internal static bool SameFormat(string sourceExtension, string target) => NormalizeFormat(sourceExtension) == NormalizeFormat(target);

    /// <summary>Extensão com ponto, minúscula (aceita com ou sem ponto).</summary>
    private static string Ext(string? extension)
    {
        string e = (extension ?? "").Trim().ToLowerInvariant();
        return e.Length == 0 || e[0] == '.' ? e : "." + e;
    }

    internal static FileCategory DetectCategory(string? extension, IReadOnlySet<string> wicReadable)
    {
        string ext = Ext(extension);
        if (ext.Length < 2) return FileCategory.Unsupported;
        if (KnownImageExts.Contains(ext) || wicReadable.Contains(ext)) return FileCategory.Image;
        if (VideoExts.Contains(ext)) return FileCategory.Video;
        if (AudioExts.Contains(ext)) return FileCategory.Audio;
        if (SpreadsheetExts.Contains(ext)) return FileCategory.Spreadsheet;
        if (PresentationExts.Contains(ext)) return FileCategory.Presentation;
        if (OfficeTextExts.Contains(ext) || PandocTextExts.Contains(ext) || HtmlExts.Contains(ext) || ext is ".txt" or ".pdf")
            return FileCategory.Document;
        return FileCategory.Unsupported;
    }

    internal static bool IsPhoto(string? extension) => PhotoExts.Contains(Ext(extension));

    private static string[] TargetsFor(string ext, FileCategory category) => category switch
    {
        FileCategory.Image => ImageTargets,
        FileCategory.Video => VideoTargets,
        FileCategory.Audio => AudioTargets,
        FileCategory.Spreadsheet => SpreadsheetTargets,
        FileCategory.Presentation => PresentationTargets,
        FileCategory.Document when OfficeTextExts.Contains(ext) => OfficeTextTargets,
        FileCategory.Document when HtmlExts.Contains(ext) => HtmlTargets,
        FileCategory.Document when ext is ".md" or ".markdown" => MarkdownTargets,
        FileCategory.Document when ext == ".epub" => EpubTargets,
        FileCategory.Document when ext == ".txt" => PlainTextTargets,
        _ => []   // PDF e desconhecidos: nenhum motor daqui converte com fidelidade
    };

    // ------------------------------------------------------------------
    // 1. Opções
    // ------------------------------------------------------------------

    internal static List<ConversionOption> GetOptions(string? sourceExtension, ConversionToolset tools)
    {
        string ext = Ext(sourceExtension);
        FileCategory category = DetectCategory(ext, tools.WicReadable);
        var options = new List<ConversionOption>();

        foreach (string target in TargetsFor(ext, category))
        {
            ConversionEngine engine = Plan(ext, target, tools);
            if (engine != ConversionEngine.None)
                options.Add(new ConversionOption(target, DisplayName(target, category), EngineName(engine)));
        }

        return options;
    }

    internal static string DisplayName(string target, FileCategory category) => (target, category) switch
    {
        ("gif", FileCategory.Video) => "GIF — animação curta, sem som",
        ("mp3", FileCategory.Video) => "MP3 — só o áudio",
        ("m4a", FileCategory.Video) => "M4A — só o áudio (AAC)",
        ("wav", FileCategory.Video) => "WAV — só o áudio, sem compressão",
        _ => Names.TryGetValue(target, out string? name) ? name : target.ToUpperInvariant()
    };

    internal static string EngineName(ConversionEngine engine) => engine switch
    {
        ConversionEngine.Wic => "Windows",
        ConversionEngine.Magick => "ImageMagick",
        ConversionEngine.FFmpeg => "FFmpeg",
        ConversionEngine.Pandoc => "Pandoc",
        ConversionEngine.LibreOffice => "LibreOffice",
        ConversionEngine.PandocThenLibreOffice => "Pandoc + LibreOffice",
        _ => ""
    };

    // ------------------------------------------------------------------
    // 2. Quem converte
    // ------------------------------------------------------------------

    internal static ConversionEngine Plan(string? sourceExtension, string? targetFormat, ConversionToolset tools)
    {
        string ext = Ext(sourceExtension);
        string target = NormalizeFormat(targetFormat);
        FileCategory category = DetectCategory(ext, tools.WicReadable);

        // Só destinos do catálogo daquela origem, e nunca o mesmo formato.
        if (!TargetsFor(ext, category).Contains(target) || SameFormat(ext, target)) return ConversionEngine.None;

        return category switch
        {
            FileCategory.Image => PlanImage(ext, target, tools),
            FileCategory.Video or FileCategory.Audio =>
                FfmpegCodecArguments(target, category == FileCategory.Video, tools) is null ? ConversionEngine.None : ConversionEngine.FFmpeg,
            _ => PlanDocument(ext, target, category, tools)
        };
    }

    private static ConversionEngine PlanImage(string ext, string target, ConversionToolset tools)
    {
        if (tools.WicReadable.Contains(ext))
        {
            if (target is "png" or "jpg" or "pdf" or "ico" or "gif" or "bmp" or "tiff") return ConversionEngine.Wic;
            if (target == "heic" && tools.CanWriteHeic) return ConversionEngine.Wic;
        }

        if (tools.HasMagick) return ConversionEngine.Magick;

        bool ffmpegWrites = target switch
        {
            "png" => tools.HasEncoder("png"),
            "jpg" => tools.HasEncoder("mjpeg"),
            "webp" => tools.HasEncoder("libwebp"),
            "bmp" => tools.HasEncoder("bmp"),
            "tiff" => tools.HasEncoder("tiff"),
            "gif" => tools.HasEncoder("gif"),
            _ => false
        };
        return FfmpegImageReadable.Contains(ext) && ffmpegWrites ? ConversionEngine.FFmpeg : ConversionEngine.None;
    }

    private static ConversionEngine PlanDocument(string ext, string target, FileCategory category, ConversionToolset tools)
    {
        bool office = tools.HasLibreOffice, pandoc = tools.HasPandoc;

        if (category is FileCategory.Spreadsheet or FileCategory.Presentation || ext == ".txt")
            return office ? ConversionEngine.LibreOffice : ConversionEngine.None;

        if (PandocTextExts.Contains(ext))   // Markdown e EPUB: só o Pandoc lê
        {
            if (target == "pdf") return pandoc && office ? ConversionEngine.PandocThenLibreOffice : ConversionEngine.None;
            return pandoc ? ConversionEngine.Pandoc : ConversionEngine.None;
        }

        // HTML e Word/ODT/RTF: o LibreOffice preserva melhor o layout; o Pandoc cobre o resto.
        // TXT só pelo Pandoc: a exportação para texto do LibreOffice trava em documentos com tabela (verificado na 26.8).
        bool pandocReads = ext != ".doc";
        if (target is "md" or "epub" or "txt") return pandoc && pandocReads ? ConversionEngine.Pandoc : ConversionEngine.None;
        if (office) return ConversionEngine.LibreOffice;
        return pandoc && pandocReads && target != "pdf" ? ConversionEngine.Pandoc : ConversionEngine.None;
    }

    // ------------------------------------------------------------------
    // 3. Argumentos
    // ------------------------------------------------------------------

    /// <summary>Codificadores e opções do FFmpeg para um destino de áudio/vídeo; null se o FFmpeg não tem o necessário.</summary>
    internal static string[]? FfmpegCodecArguments(string target, bool fromVideo, ConversionToolset t)
    {
        string[]? h264 = t.HasEncoder("libx264") ? ["-c:v", "libx264", "-preset", "medium", "-crf", "22", "-pix_fmt", "yuv420p"]
                       : t.HasEncoder("mpeg4") ? ["-c:v", "mpeg4", "-q:v", "3"]
                       : null;
        string[]? aac = t.HasEncoder("aac") ? ["-c:a", "aac", "-b:a", "160k"] : null;

        static string[]? Join(params string[]?[] parts) => parts.Any(p => p is null) ? null : parts.SelectMany(p => p!).ToArray();

        if (fromVideo)
        {
            switch (target)
            {
                case "mp4":
                case "mov":
                    return Join(h264, aac, ["-movflags", "+faststart"]);
                case "mkv":
                    return Join(h264, aac);
                case "webm":
                    string[]? vp9 = t.HasEncoder("libvpx-vp9") ? ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-pix_fmt", "yuv420p"] : null;
                    string[]? webAudio = t.HasEncoder("libopus") ? ["-c:a", "libopus", "-b:a", "128k"]
                                       : t.HasEncoder("libvorbis") ? ["-c:a", "libvorbis", "-q:a", "5"]
                                       : null;
                    return Join(vp9, webAudio);
                case "gif":
                    // Paleta calculada do próprio vídeo: cores muito melhores que a paleta fixa do GIF.
                    return t.HasEncoder("gif")
                        ? ["-an", "-vf", "fps=12,scale='min(480,iw)':-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4", "-loop", "0"]
                        : null;
            }
        }

        // Áudio (de um áudio, ou extraído de um vídeo). -vn também descarta a capa embutida.
        string[]? audio = target switch
        {
            "mp3" => t.HasEncoder("libmp3lame") ? ["-c:a", "libmp3lame", "-q:a", "2"] : null,
            "m4a" => t.HasEncoder("aac") ? ["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"] : null,
            "aac" => t.HasEncoder("aac") ? ["-c:a", "aac", "-b:a", "192k"] : null,
            "wav" => t.HasEncoder("pcm_s16le") ? ["-c:a", "pcm_s16le"] : null,
            "aiff" => t.HasEncoder("pcm_s16be") ? ["-c:a", "pcm_s16be"] : null,
            "flac" => t.HasEncoder("flac") ? ["-c:a", "flac"] : null,
            "ogg" => t.HasEncoder("libvorbis") ? ["-c:a", "libvorbis", "-q:a", "5"] : null,
            "opus" => t.HasEncoder("libopus") ? ["-c:a", "libopus", "-b:a", "128k"] : null,
            _ => null
        };
        return audio is null ? null : ["-vn", .. audio];
    }

    internal static List<string>? BuildFfmpegMediaArguments(string source, string target, bool fromVideo, ConversionToolset tools, string destination)
    {
        string[]? codec = FfmpegCodecArguments(target, fromVideo, tools);
        if (codec is null) return null;

        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", source };
        args.AddRange(codec);
        args.Add(destination);
        return args;
    }

    internal static List<string> BuildFfmpegImageArguments(string source, string target, string destination)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", source, "-frames:v", "1" };
        if (target == "jpg") args.AddRange(["-q:v", "2"]);
        if (target == "webp") args.AddRange(["-c:v", "libwebp", "-quality", "90"]);
        if (target is "png" or "jpg" or "bmp" or "tiff") args.AddRange(["-update", "1"]);   // um arquivo só, não uma sequência
        args.Add(destination);
        return args;
    }

    internal static List<string> BuildMagickArguments(string source, string target, string destination)
    {
        var args = new List<string> { source, "-auto-orient" };
        if (target == "ico") args.AddRange(["-define", "icon:auto-resize=256,128,64,48,32,24,16"]);
        if (target is "jpg" or "webp" or "heic") args.AddRange(["-quality", "90"]);
        args.Add(destination);
        return args;
    }

    /// <summary>Filtro de exportação do LibreOffice (nomes da documentação oficial de conversão).</summary>
    internal static string LibreOfficeExportFilter(string target, FileCategory category) => target switch
    {
        "pdf" => category switch
        {
            FileCategory.Spreadsheet => "pdf:calc_pdf_Export",
            FileCategory.Presentation => "pdf:impress_pdf_Export",
            _ => "pdf:writer_pdf_Export"
        },
        "docx" => "docx:MS Word 2007 XML",
        "odt" => "odt:writer8",
        "rtf" => "rtf:Rich Text Format",
        "html" => "html:HTML (StarWriter)",
        "xlsx" => "xlsx:Calc Office Open XML",
        "ods" => "ods:calc8",
        "csv" => "csv:Text - txt - csv (StarCalc):44,34,76,1",   // vírgula, aspas, UTF-8
        "pptx" => "pptx:Impress MS PowerPoint 2007 XML",
        "odp" => "odp:impress8",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Destino sem filtro do LibreOffice.")
    };

    /// <summary>
    /// Filtro de importação, quando a detecção automática erra: HTML abriria como "Writer/Web" (sem exportar
    /// para DOCX/PDF), TXT sairia na codificação do sistema e CSV com o separador errado.
    /// </summary>
    internal static string? LibreOfficeImportFilter(string sourceExtension, char csvSeparator) => Ext(sourceExtension) switch
    {
        ".html" or ".htm" => "HTML (StarWriter)",
        ".txt" => "Text (encoded):UTF8",
        ".csv" => $"Text - txt - csv (StarCalc):{(int)csvSeparator},34,76,1",
        _ => null
    };

    internal static List<string> BuildLibreOfficeArguments(string source, string sourceExtension, string target,
        string profileDirectoryUri, string outputDirectory, char csvSeparator = ',')
    {
        FileCategory category = DetectCategory(sourceExtension, BuiltInWicReadable);
        var args = new List<string>
        {
            $"-env:UserInstallation={profileDirectoryUri}",   // perfil próprio: não colide com um LibreOffice aberto
            "--headless", "--norestore", "--nolockcheck"
        };

        string? importFilter = LibreOfficeImportFilter(sourceExtension, csvSeparator);
        if (importFilter is not null) args.Add($"--infilter={importFilter}");

        args.AddRange(["--convert-to", LibreOfficeExportFilter(target, category), "--outdir", outputDirectory, source]);
        return args;
    }

    /// <summary>Leitor e escritor explícitos: nada depende de o Pandoc adivinhar pela extensão.</summary>
    internal static List<string> BuildPandocArguments(string source, string sourceExtension, string target, string destination)
    {
        string reader = Ext(sourceExtension) switch
        {
            ".md" or ".markdown" => "markdown",
            ".html" or ".htm" => "html",
            ".docx" => "docx",
            ".odt" => "odt",
            ".rtf" => "rtf",
            ".epub" => "epub",
            var e => throw new ArgumentOutOfRangeException(nameof(sourceExtension), e, "O Pandoc não lê este formato.")
        };
        string writer = target switch
        {
            "md" => "gfm",
            "txt" => "plain",
            "html" => "html5",
            "docx" or "odt" or "rtf" or "epub" => target,
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "O Pandoc não grava este formato aqui.")
        };

        var args = new List<string> { "-f", reader, "-t", writer, "-s" };
        if (target is "md" or "txt") args.Add("--wrap=none");   // um parágrafo por linha, sem quebras artificiais
        args.AddRange([source, "-o", destination]);
        return args;
    }

    /// <summary>Separador de um CSV pela primeira linha: ";" (comum no Brasil), tabulação ou ",".</summary>
    internal static char DetectCsvSeparator(string? firstLine)
    {
        if (string.IsNullOrEmpty(firstLine)) return ',';

        int commas = 0, semicolons = 0, tabs = 0;
        bool quoted = false;
        foreach (char c in firstLine)
        {
            if (c == '"') quoted = !quoted;
            else if (quoted) continue;
            else if (c == ',') commas++;
            else if (c == ';') semicolons++;
            else if (c == '\t') tabs++;
        }

        if (semicolons > commas && semicolons >= tabs) return ';';
        if (tabs > commas && tabs > semicolons) return '\t';
        return ',';
    }

    private static HashSet<string> Set(params string[] items) => new(items, StringComparer.OrdinalIgnoreCase);
}
