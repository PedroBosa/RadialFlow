using System;
using System.Collections.Generic;
using System.Linq;
using RadialFlow.Services;
using Xunit;

namespace RadialFlow.Tests;

/// <summary>
/// Catálogo de conversões: o que é oferecido com cada combinação de ferramentas, quem converte e com quais
/// argumentos. Puro: nenhuma ferramenta precisa estar instalada.
/// </summary>
public sealed class ConversionFormatsTests
{
    private static readonly HashSet<string> StoreCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".dib", ".gif", ".ico", ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".tif", ".tiff", ".jxr", ".wdp",
        ".webp", ".heic", ".heif", ".avif", ".cr2", ".nef", ".dng"
    };

    /// <summary>Windows puro: nenhuma ferramenta e nenhuma extensão da Store.</summary>
    private static readonly ConversionToolset Nothing = new();

    private static readonly ConversionToolset Everything = new()
    {
        WicReadable = StoreCodecs, CanWriteHeic = true,
        HasFFmpeg = true, HasMagick = true, HasPandoc = true, HasLibreOffice = true
    };

    private static string[] Offered(string ext, ConversionToolset tools) =>
        ConversionFormats.GetOptions(ext, tools).Select(o => o.TargetFormat).ToArray();

    // ------------------------------------------------------------------
    // Nada oferecido sem caminho real
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".mp3")]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".pptx")]
    [InlineData(".md")]
    [InlineData(".svg")]
    [InlineData(".webp")]   // sem a extensão WebP da Store o Windows não lê
    [InlineData(".pdf")]
    [InlineData(".exe")]
    [InlineData(".zip")]
    [InlineData("")]
    public void SemFerramentas_NadaQueNaoFuncione(string ext)
    {
        Assert.Empty(Offered(ext, Nothing));
    }

    [Fact]
    public void Pdf_NuncaTemOrigemSuportada()
    {
        Assert.Empty(Offered(".pdf", Everything));
    }

    [Fact]
    public void ImagemComum_ConverteSemInstalarNada()
    {
        Assert.Equal(new[] { "jpg", "pdf", "ico", "gif", "bmp", "tiff" }, Offered(".png", Nothing));
        Assert.Equal(new[] { "png", "pdf", "ico", "gif", "bmp", "tiff" }, Offered(".JPEG", Nothing));
        Assert.All(ConversionFormats.GetOptions(".png", Nothing), o => Assert.Equal("Windows", o.Engine));
    }

    [Fact]
    public void WebpEHeic_ComOsCodecsDaStore_SaemPeloWindows()
    {
        var tools = Nothing with { WicReadable = StoreCodecs };

        Assert.Equal(new[] { "png", "jpg", "pdf", "ico", "gif", "bmp", "tiff" }, Offered(".webp", tools));
        Assert.Contains("png", Offered(".heic", tools));
        Assert.Contains("jpg", Offered(".cr2", tools));   // RAW de câmera
        Assert.DoesNotContain("heic", Offered(".png", tools));   // sem o codificador HEIF funcionando, não oferece

        Assert.Contains("heic", Offered(".png", tools with { CanWriteHeic = true }));
        Assert.DoesNotContain("heic", Offered(".heif", tools with { CanWriteHeic = true }));   // mesmo formato
    }

    [Fact]
    public void Webp_ComoDestino_ExigeImageMagickOuFfmpeg()
    {
        Assert.DoesNotContain("webp", Offered(".png", Nothing));
        Assert.Equal(ConversionEngine.Magick, ConversionFormats.Plan(".png", "webp", Nothing with { HasMagick = true }));

        var ffmpeg = Nothing with { HasFFmpeg = true, FfmpegEncoders = new HashSet<string> { "libwebp", "png" } };
        Assert.Equal(ConversionEngine.FFmpeg, ConversionFormats.Plan(".png", "webp", ffmpeg));
        Assert.Equal(ConversionEngine.None, ConversionFormats.Plan(".png", "webp", ffmpeg with { FfmpegEncoders = new HashSet<string> { "png" } }));
    }

    [Fact]
    public void ImagemQueOWindowsNaoLe_UsaImageMagick()
    {
        Assert.Empty(Offered(".svg", Nothing));
        Assert.Equal(ConversionEngine.Magick, ConversionFormats.Plan(".svg", "png", Nothing with { HasMagick = true }));
        Assert.Empty(Offered(".jxl", Nothing with { WicReadable = StoreCodecs }));
    }

    [Fact]
    public void NuncaOfereceOMesmoFormato_NemComNomeDiferente()
    {
        foreach (string ext in new[] { ".jpeg", ".jpg", ".tif", ".htm", ".markdown", ".aif", ".oga", ".heif" })
        {
            string canonical = ConversionFormats.NormalizeFormat(ext);
            Assert.DoesNotContain(canonical, Offered(ext, Everything));
        }
    }

    [Fact]
    public void ARodaComportaTodas_ListaNuncaPassaDe8()
    {
        string[] samples = [".png", ".jpg", ".webp", ".heic", ".mp4", ".mkv", ".mp3", ".wav", ".docx", ".doc", ".odt", ".rtf",
                            ".txt", ".html", ".md", ".epub", ".xlsx", ".csv", ".pptx", ".odp"];
        foreach (string ext in samples)
        {
            var options = ConversionFormats.GetOptions(ext, Everything);
            Assert.InRange(options.Count, 1, 8);
            Assert.All(options, o => Assert.False(string.IsNullOrWhiteSpace(o.DisplayName)));
            Assert.Equal(options.Count, options.Select(o => o.TargetFormat).Distinct().Count());
        }
    }

    // ------------------------------------------------------------------
    // Documentos
    // ------------------------------------------------------------------

    [Fact]
    public void Markdown_SoComPandoc_NaoOferecePdf()
    {
        // Antes: PDF pelo Pandoc, que exige LaTeX e falhava em quase todo PC.
        var pandoc = Nothing with { HasPandoc = true };

        Assert.Equal(new[] { "docx", "html", "odt", "rtf", "epub", "txt" }, Offered(".md", pandoc));
        Assert.Equal(ConversionEngine.PandocThenLibreOffice, ConversionFormats.Plan(".md", "pdf", pandoc with { HasLibreOffice = true }));
    }

    [Fact]
    public void Word_ComLibreOffice_UsaOLibreOffice_EMarkdownPeloPandoc()
    {
        var office = Nothing with { HasLibreOffice = true };

        // Sem TXT: a exportação para texto do LibreOffice trava em documentos com tabela; TXT vem do Pandoc.
        Assert.Equal(new[] { "pdf", "odt", "rtf", "html" }, Offered(".docx", office));
        Assert.Equal(new[] { "pdf", "docx", "odt", "rtf", "html" }, Offered(".doc", office));
        Assert.Equal(ConversionEngine.Pandoc, ConversionFormats.Plan(".docx", "txt", Everything));
        Assert.Equal(ConversionEngine.None, ConversionFormats.Plan(".doc", "txt", Everything));
        Assert.Equal(ConversionEngine.Pandoc, ConversionFormats.Plan(".docx", "md", Everything));
        Assert.Equal(ConversionEngine.None, ConversionFormats.Plan(".doc", "md", Everything));   // o Pandoc não lê .doc
        Assert.Equal(ConversionEngine.LibreOffice, ConversionFormats.Plan(".docx", "pdf", Everything));
    }

    [Fact]
    public void Word_SoComPandoc_OfereceOQueOPandocFaz_SemPdf()
    {
        Assert.Equal(new[] { "odt", "rtf", "txt", "html", "md", "epub" }, Offered(".docx", Nothing with { HasPandoc = true }));
    }

    [Fact]
    public void PlanilhaEApresentacao_PrecisamDoLibreOffice()
    {
        var office = Nothing with { HasLibreOffice = true };

        Assert.Empty(Offered(".xlsx", Nothing with { HasPandoc = true }));
        Assert.Equal(new[] { "pdf", "ods", "csv" }, Offered(".xlsx", office));
        Assert.Equal(new[] { "pdf", "xlsx", "ods" }, Offered(".csv", office));
        Assert.Equal(new[] { "pdf", "odp" }, Offered(".pptx", office));
        Assert.Equal(new[] { "pdf", "pptx", "odp" }, Offered(".ppt", office));
    }

    [Theory]
    [InlineData(".docx", "pdf", "pdf:writer_pdf_Export")]
    [InlineData(".xlsx", "pdf", "pdf:calc_pdf_Export")]
    [InlineData(".pptx", "pdf", "pdf:impress_pdf_Export")]
    [InlineData(".odt", "docx", "docx:MS Word 2007 XML")]
    [InlineData(".xlsx", "csv", "csv:Text - txt - csv (StarCalc):44,34,76,1")]
    [InlineData(".ods", "xlsx", "xlsx:Calc Office Open XML")]
    [InlineData(".odp", "pptx", "pptx:Impress MS PowerPoint 2007 XML")]
    public void LibreOffice_FiltroDeExportacaoExplicito(string ext, string target, string expected)
    {
        var args = ConversionFormats.BuildLibreOfficeArguments(@"C:\docs\a" + ext, ext, target, "file:///C:/perfil", @"C:\saida");

        int index = args.IndexOf("--convert-to");
        Assert.Equal(expected, args[index + 1]);
        Assert.Equal(@"C:\docs\a" + ext, args[^1]);   // o arquivo é o último argumento
        Assert.Equal("-env:UserInstallation=file:///C:/perfil", args[0]);
        Assert.Contains("--headless", args);
    }

    [Theory]
    [InlineData(".html", ',', "--infilter=HTML (StarWriter)")]
    [InlineData(".txt", ',', "--infilter=Text (encoded):UTF8")]
    [InlineData(".csv", ';', "--infilter=Text - txt - csv (StarCalc):59,34,76,1")]
    [InlineData(".csv", '\t', "--infilter=Text - txt - csv (StarCalc):9,34,76,1")]
    public void LibreOffice_FiltroDeImportacaoQuandoADeteccaoErraria(string ext, char separator, string expected)
    {
        var args = ConversionFormats.BuildLibreOfficeArguments(@"C:\docs\a" + ext, ext, "pdf", "file:///C:/p", @"C:\o", separator);

        Assert.Contains(expected, args);
    }

    [Fact]
    public void LibreOffice_DocxSemFiltroDeImportacao()
    {
        var args = ConversionFormats.BuildLibreOfficeArguments(@"C:\docs\a.docx", ".docx", "pdf", "file:///C:/p", @"C:\o");

        Assert.DoesNotContain(args, a => a.StartsWith("--infilter", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("nome;idade;cidade", ';')]
    [InlineData("nome,idade,cidade", ',')]
    [InlineData("nome\tidade\tcidade", '\t')]
    [InlineData("\"Silva, João\";30;\"Rio\"", ';')]   // vírgula entre aspas não conta
    [InlineData("valor", ',')]
    [InlineData("", ',')]
    [InlineData(null, ',')]
    public void DetectCsvSeparator_PelaPrimeiraLinha(string? line, char expected)
    {
        Assert.Equal(expected, ConversionFormats.DetectCsvSeparator(line));
    }

    [Fact]
    public void Pandoc_LeitorEEscritorExplicitos()
    {
        Assert.Equal(new[] { "-f", "markdown", "-t", "docx", "-s", @"C:\a.md", "-o", @"C:\a.docx" },
            ConversionFormats.BuildPandocArguments(@"C:\a.md", ".md", "docx", @"C:\a.docx"));
        Assert.Equal(new[] { "-f", "docx", "-t", "gfm", "-s", "--wrap=none", @"C:\a.docx", "-o", @"C:\a.md" },
            ConversionFormats.BuildPandocArguments(@"C:\a.docx", ".docx", "md", @"C:\a.md"));
        Assert.Equal(new[] { "-f", "html", "-t", "plain", "-s", "--wrap=none", @"C:\a.htm", "-o", @"C:\a.txt" },
            ConversionFormats.BuildPandocArguments(@"C:\a.htm", ".htm", "txt", @"C:\a.txt"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConversionFormats.BuildPandocArguments(@"C:\a.md", ".md", "pdf", @"C:\a.pdf"));
    }

    // ------------------------------------------------------------------
    // Áudio e vídeo
    // ------------------------------------------------------------------

    [Fact]
    public void Video_ComFfmpegCompleto_TodosOsDestinos()
    {
        var ffmpeg = Nothing with { HasFFmpeg = true };

        Assert.Equal(new[] { "mkv", "webm", "mov", "gif", "mp3", "m4a", "wav" }, Offered(".mp4", ffmpeg));
        Assert.Equal(new[] { "mp3", "m4a", "wav", "flac", "ogg", "opus", "aac", "aiff" }, Offered(".wma", ffmpeg));
        Assert.Equal("MP3 — só o áudio", ConversionFormats.GetOptions(".mov", ffmpeg).Single(o => o.TargetFormat == "mp3").DisplayName);
    }

    [Fact]
    public void Video_SoOfereceOQueOFfmpegInstaladoCodifica()
    {
        // Build mínima: sem libx264, libvpx, libmp3lame, libvorbis e libopus.
        var minimal = Nothing with
        {
            HasFFmpeg = true,
            FfmpegEncoders = new HashSet<string> { "mpeg4", "aac", "gif", "pcm_s16le", "pcm_s16be", "flac" }
        };

        Assert.Equal(new[] { "mp4", "mkv", "mov", "gif", "m4a", "wav" }, Offered(".avi", minimal));
        Assert.Equal(new[] { "m4a", "wav", "flac", "aac", "aiff" }, Offered(".mp3", minimal));

        var args = ConversionFormats.BuildFfmpegMediaArguments(@"C:\v.avi", "mp4", fromVideo: true, minimal, @"C:\v.mp4")!;
        Assert.Equal(new[] { "-c:v", "mpeg4" }, args.SkipWhile(a => a != "-c:v").Take(2));   // sem H.264, cai no MPEG-4
    }

    [Fact]
    public void FfmpegMp4_H264ComAacEInicioRapido()
    {
        var args = ConversionFormats.BuildFfmpegMediaArguments(@"C:\v.mkv", "mp4", true, Nothing with { HasFFmpeg = true }, @"C:\v.mp4")!;

        Assert.Equal(new[] { "-hide_banner", "-nostdin", "-y", "-i", @"C:\v.mkv" }, args.Take(5));
        Assert.Equal(@"C:\v.mp4", args[^1]);
        Assert.Contains("libx264", args);
        Assert.Contains("yuv420p", args);   // 10 bits ou 4:4:4 não tocam em todo lugar
        Assert.Contains("aac", args);
        Assert.Contains("+faststart", args);
    }

    [Theory]
    [InlineData("mp3", "libmp3lame")]
    [InlineData("m4a", "aac")]
    [InlineData("wav", "pcm_s16le")]
    [InlineData("flac", "flac")]
    [InlineData("ogg", "libvorbis")]
    [InlineData("opus", "libopus")]
    [InlineData("aiff", "pcm_s16be")]
    public void FfmpegAudio_CodecCertoESemVideo(string target, string encoder)
    {
        var args = ConversionFormats.BuildFfmpegMediaArguments(@"C:\a.wav", target, false, Nothing with { HasFFmpeg = true }, @"C:\a." + target)!;

        Assert.Contains("-vn", args);
        Assert.Equal(encoder, args[args.IndexOf("-c:a") + 1]);
    }

    [Fact]
    public void FfmpegGif_UsaPaletaPropriaESemSom()
    {
        var args = ConversionFormats.BuildFfmpegMediaArguments(@"C:\v.mp4", "gif", true, Nothing with { HasFFmpeg = true }, @"C:\v.gif")!;

        Assert.Contains("-an", args);
        Assert.Contains(args, a => a.Contains("palettegen", StringComparison.Ordinal) && a.Contains("paletteuse", StringComparison.Ordinal));
    }

    [Fact]
    public void FfmpegEncoders_LeAListaEIgnoraExperimentais()
    {
        string[] output =
        [
            "Encoders:",
            " V..... = Video",
            " ...X.. = Codec is experimental",
            " ------",
            " V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)",
            " A....D aac                  AAC (Advanced Audio Coding)",
            " A..X.D opus                 Opus",
            " A....D libopus              libopus Opus (codec opus)",
            " S..... srt                  SubRip subtitle",
            "",
        ];

        var encoders = FfmpegEncoders.Parse(output);

        Assert.Equal(new[] { "aac", "libopus", "libx264", "srt" }, encoders.OrderBy(e => e, StringComparer.Ordinal));
    }

    [Fact]
    public void FfmpegEncoders_LinhasAntesDoSeparadorNaoContam()
    {
        Assert.Empty(FfmpegEncoders.Parse([" V..... = Video", " A....D aac  AAC"]));
    }
}
