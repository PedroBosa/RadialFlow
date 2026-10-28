using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RadialFlow.Services;
using RadialFlow.Tests.TestSupport;
using Xunit;

namespace RadialFlow.Tests;

/// <summary>Conversão de imagem pelo Windows, com arquivos reais numa pasta temporária.</summary>
public sealed class ImageConverterTests : IDisposable
{
    // 16 x 8: metade esquerda vermelha, direita azul (gerados com o ffmpeg: WebP sem perda; AVIF 4:2:0, o layout de
    // câmeras e celulares; o codec do Windows não decodifica AVIF 4:4:4 sem perda). As amostras ficam em
    // StoreImageFixtures para a sonda do atributo decodificar exatamente o mesmo arquivo.

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ------------------------------------------------------------------
    // Utilitários
    // ------------------------------------------------------------------

    /// <summary>Imagem BGRA de teste: cada pixel recebe a cor de <paramref name="color"/>.</summary>
    private static BitmapSource Image(int width, int height, Func<int, int, (byte B, byte G, byte R, byte A)> color)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var (b, g, r, a) = color(x, y);
                int i = (y * width + x) * 4;
                pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = a;
            }

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static readonly (byte, byte, byte, byte) Red = (0, 0, 255, 255);
    private static readonly (byte, byte, byte, byte) Blue = (255, 0, 0, 255);
    private static readonly (byte, byte, byte, byte) Clear = (0, 0, 0, 0);

    private string Save(BitmapSource image, string name, BitmapEncoder? encoder = null, BitmapMetadata? metadata = null)
    {
        encoder ??= new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image, null, metadata, null));
        string path = _temp.Combine(name);
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static BitmapFrame Decode(string path)
    {
        using var stream = File.OpenRead(path);
        return BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
    }

    private static (byte B, byte G, byte R, byte A) PixelAt(BitmapSource image, int x, int y)
    {
        byte[] p = IcoFile.GetPixels(ImageConverter.ToBgra32(image));
        int i = (y * image.PixelWidth + x) * 4;
        return (p[i], p[i + 1], p[i + 2], p[i + 3]);
    }

    private static void AssertNear((byte B, byte G, byte R, byte A) actual, (byte B, byte G, byte R, byte A) expected, int tolerance = 12)
    {
        Assert.InRange(actual.B, Math.Max(0, expected.B - tolerance), Math.Min(255, expected.B + tolerance));
        Assert.InRange(actual.G, Math.Max(0, expected.G - tolerance), Math.Min(255, expected.G + tolerance));
        Assert.InRange(actual.R, Math.Max(0, expected.R - tolerance), Math.Min(255, expected.R + tolerance));
        Assert.InRange(actual.A, Math.Max(0, expected.A - tolerance), Math.Min(255, expected.A + tolerance));
    }

    private string HalfRedHalfBluePng() => Save(Image(40, 20, (x, _) => x < 20 ? Red : Blue), "metade.png");

    // ------------------------------------------------------------------
    // Formatos do Windows
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("jpg")]
    [InlineData("bmp")]
    [InlineData("gif")]
    [InlineData("tiff")]
    public void Png_ParaFormatosDoWindows_SaiLegivelComAsMesmasCores(string target)
    {
        string source = HalfRedHalfBluePng();
        string destination = _temp.Combine("saida." + target);

        ImageConverter.Convert(source, target, destination);

        BitmapFrame result = Decode(destination);
        Assert.Equal((40, 20), (result.PixelWidth, result.PixelHeight));
        AssertNear(PixelAt(result, 5, 10), Red, tolerance: 40);    // JPG e GIF aproximam as cores
        AssertNear(PixelAt(result, 35, 10), Blue, tolerance: 40);
    }

    [Fact]
    public void Jpg_ParaPng_PreservaAsDimensoes()
    {
        string jpg = Save(Image(33, 17, (x, _) => x < 16 ? Red : Blue), "foto.jpg", new JpegBitmapEncoder());
        string destination = _temp.Combine("foto.png");

        ImageConverter.Convert(jpg, "png", destination);

        Assert.Equal((33, 17), (Decode(destination).PixelWidth, Decode(destination).PixelHeight));
    }

    [Theory]
    [InlineData("jpg")]
    [InlineData("bmp")]
    public void Transparente_ParaFormatoSemAlfa_FundoBrancoNaoPreto(string target)
    {
        string source = Save(Image(8, 8, (_, _) => Clear), "vazio.png");
        string destination = _temp.Combine("vazio." + target);

        ImageConverter.Convert(source, target, destination);

        AssertNear(PixelAt(Decode(destination), 4, 4), (255, 255, 255, 255), tolerance: 3);
    }

    [Fact]
    public void Transparente_ParaPng_MantemATransparencia()
    {
        string source = Save(Image(8, 8, (x, _) => x < 4 ? Clear : Red), "meio.tiff", new TiffBitmapEncoder());
        string destination = _temp.Combine("meio.png");

        ImageConverter.Convert(source, "png", destination);

        Assert.Equal(0, PixelAt(Decode(destination), 1, 1).A);
        Assert.Equal(255, PixelAt(Decode(destination), 6, 1).A);
    }

    [Fact]
    public void Png16Bits_ParaTiff_Continua16Bits()
    {
        var pixels = new ushort[4 * 4 * 3];
        Array.Fill(pixels, (ushort)40000);
        var deep = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Rgb48, null, pixels, 4 * 6);
        string source = Save(deep, "profundo.png");
        string destination = _temp.Combine("profundo.tiff");

        ImageConverter.Convert(source, "tiff", destination);

        Assert.Equal(48, Decode(destination).Format.BitsPerPixel);
    }

    [Fact]
    public void Destino_NuncaSobrescreveArquivoExistente()
    {
        string source = HalfRedHalfBluePng();
        string existing = _temp.CreateFile("ja-existe.jpg");
        File.WriteAllText(existing, "do usuario");

        Assert.Throws<IOException>(() => ImageConverter.Convert(source, "jpg", existing));
        Assert.Equal("do usuario", File.ReadAllText(existing));
    }

    [Fact]
    public void ArquivoQueNaoEImagem_FalhaComMensagemClara()
    {
        string fake = _temp.CreateFile("falso.png");
        File.WriteAllText(fake, "isto não é uma imagem");

        var error = Assert.Throws<InvalidOperationException>(() => ImageConverter.Convert(fake, "jpg", _temp.Combine("x.jpg")));
        Assert.Contains("não conseguiu abrir", error.Message);
        Assert.False(File.Exists(_temp.Combine("x.jpg")));
    }

    // ------------------------------------------------------------------
    // Orientação EXIF
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void ApplyOrientation_SegueATabelaDoExif(int orientation)
    {
        const int w = 3, h = 2;
        BitmapSource stored = Image(w, h, (x, y) => ((byte)(y * w + x), 0, 0, 255));   // cada pixel guarda o próprio índice

        BitmapSource shown = ImageConverter.ApplyOrientation(stored, orientation);

        // Onde cada pixel EXIBIDO (X, Y) estava na imagem gravada (tabela da especificação EXIF/TIFF).
        Func<int, int, (int X, int Y)> from = orientation switch
        {
            1 => (X, Y) => (X, Y),
            2 => (X, Y) => (w - 1 - X, Y),
            3 => (X, Y) => (w - 1 - X, h - 1 - Y),
            4 => (X, Y) => (X, h - 1 - Y),
            5 => (X, Y) => (Y, X),
            6 => (X, Y) => (Y, h - 1 - X),
            7 => (X, Y) => (w - 1 - Y, h - 1 - X),
            _ => (X, Y) => (w - 1 - Y, X),
        };

        bool swapped = orientation >= 5;
        Assert.Equal(swapped ? (h, w) : (w, h), (shown.PixelWidth, shown.PixelHeight));
        for (int Y = 0; Y < shown.PixelHeight; Y++)
            for (int X = 0; X < shown.PixelWidth; X++)
            {
                var (sx, sy) = from(X, Y);
                Assert.Equal(sy * w + sx, PixelAt(shown, X, Y).B);
            }
    }

    [Fact]
    public void FotoDeCelularDeitada_SaiEmPe()
    {
        // TIFF (sem perda) marcado com orientação 6: "gire 90° no sentido horário para exibir".
        var metadata = new BitmapMetadata("tiff");
        metadata.SetQuery("/ifd/{ushort=274}", (ushort)6);
        string source = Save(Image(30, 20, (x, y) => x < 10 && y < 10 ? Red : Blue), "celular.tiff", new TiffBitmapEncoder(), metadata);
        string destination = _temp.Combine("celular.png");

        ImageConverter.Convert(source, "png", destination);

        BitmapFrame result = Decode(destination);
        Assert.Equal((20, 30), (result.PixelWidth, result.PixelHeight));
        AssertNear(PixelAt(result, 15, 3), Red, tolerance: 0);   // o canto de cima à esquerda foi para cima à direita
        AssertNear(PixelAt(result, 3, 3), Blue, tolerance: 0);
    }

    [Fact]
    public void JpegComOrientacao_TrocaLarguraEAltura()
    {
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)8);
        string source = Save(Image(30, 20, (_, _) => Red), "deitada.jpg", new JpegBitmapEncoder(), metadata);
        string destination = _temp.Combine("deitada.png");

        ImageConverter.Convert(source, "png", destination);

        Assert.Equal((20, 30), (Decode(destination).PixelWidth, Decode(destination).PixelHeight));
    }

    // ------------------------------------------------------------------
    // ICO
    // ------------------------------------------------------------------

    [Fact]
    public void Ico_TodosOsTamanhos_QuadradosEProporcaoMantida()
    {
        string source = Save(Image(300, 150, (_, _) => Red), "faixa.png");
        string destination = _temp.Combine("faixa.ico");

        ImageConverter.Convert(source, "ico", destination);

        using var stream = File.OpenRead(destination);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.Equal(new[] { 16, 24, 32, 48, 64, 128, 256 }, decoder.Frames.Select(f => f.PixelWidth).OrderBy(s => s));
        Assert.All(decoder.Frames, f => Assert.Equal(f.PixelWidth, f.PixelHeight));

        BitmapFrame big = decoder.Frames.Single(f => f.PixelWidth == 256);
        Assert.Equal(0, PixelAt(big, 128, 10).A);              // faixa 2:1 centrada: em cima fica transparente
        AssertNear(PixelAt(big, 128, 128), Red, tolerance: 2);  // e o meio é a imagem
    }

    [Fact]
    public void Ico_ImagemPequena_NaoInventaTamanhosEnormes()
    {
        var frames = ImageConverter.BuildIconFrames(Image(20, 20, (_, _) => Blue));

        Assert.Equal(new[] { 48, 32, 24, 16 }, frames.Select(f => f.PixelWidth));
    }

    [Fact]
    public void Resize_MediaDaArea_SemEscurecerBordasTransparentes()
    {
        // 4x1: vermelho opaco, transparente, vermelho opaco, transparente -> 2x1: vermelho com alfa pela metade.
        BitmapSource image = Image(4, 1, (x, _) => x % 2 == 0 ? Red : Clear);

        byte[] half = ImageConverter.Resize(image, 2, 1);

        Assert.Equal(new byte[] { 0, 0, 255, 128, 0, 0, 255, 128 }, half);
    }

    // ------------------------------------------------------------------
    // PDF
    // ------------------------------------------------------------------

    /// <summary>Confere a estrutura: cabeçalho, objetos nos offsets da tabela xref e startxref apontando para ela.</summary>
    private static string ValidatePdfStructure(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-1.4", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);

        Match start = Regex.Match(text, @"startxref\n(\d+)\n%%EOF\n$");
        Assert.True(start.Success);
        int xref = int.Parse(start.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("xref", text.Substring(xref, 4));

        MatchCollection entries = Regex.Matches(text[xref..], @"(\d{10}) 00000 n\r\n");
        Assert.Equal(6, entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            int offset = int.Parse(entries[i].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.StartsWith($"{i + 1} 0 obj", text[offset..], StringComparison.Ordinal);
        }

        return text;
    }

    private static byte[] ImageStream(byte[] pdf, string text)
    {
        Match length = Regex.Match(text, @"/Subtype /Image .*?/Length (\d+) >>\nstream\n");
        Assert.True(length.Success);
        int start = length.Index + length.Length;
        return pdf.AsSpan(start, int.Parse(length.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }

    [Fact]
    public void Pdf_DeCaptura_SemPerda_PaginaDoTamanhoDaImagem()
    {
        BitmapSource image = Image(40, 20, (x, _) => x < 20 ? Red : Clear);

        byte[] pdf = PdfImageWriter.Build(image, photo: false);

        string text = ValidatePdfStructure(pdf);
        Assert.Contains("/MediaBox [0 0 30 15]", text);   // 96 dpi: 40 px = 30 pt
        Assert.Contains("/Filter /FlateDecode", text);

        using var zlib = new ZLibStream(new MemoryStream(ImageStream(pdf, text)), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        byte[] rgb = raw.ToArray();
        Assert.Equal(40 * 20 * 3, rgb.Length);
        Assert.Equal(new byte[] { 255, 0, 0 }, rgb[..3]);                        // vermelho exato (RGB)
        Assert.Equal(new byte[] { 255, 255, 255 }, rgb[(39 * 3)..(40 * 3)]);    // transparente virou branco
    }

    [Fact]
    public void Pdf_DeFoto_JpegEmbutidoQueDecodifica()
    {
        BitmapSource image = Image(64, 48, (_, _) => Blue);

        byte[] pdf = PdfImageWriter.Build(image, photo: true);

        string text = ValidatePdfStructure(pdf);
        Assert.Contains("/Filter /DCTDecode", text);
        using var jpeg = new MemoryStream(ImageStream(pdf, text));
        BitmapFrame embedded = BitmapDecoder.Create(jpeg, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal((64, 48), (embedded.PixelWidth, embedded.PixelHeight));
        AssertNear(PixelAt(embedded, 30, 20), Blue, tolerance: 8);
    }

    [Fact]
    public void Pdf_ResolucaoDoArquivo_DefineOTamanhoDaPagina()
    {
        var pixels = new byte[300 * 150 * 4];
        var print = BitmapSource.Create(300, 150, 300, 300, PixelFormats.Bgra32, null, pixels, 300 * 4);   // 300 dpi: 1 x 0,5 polegada

        string text = Encoding.Latin1.GetString(PdfImageWriter.Build(print, photo: false));

        Assert.Contains("/MediaBox [0 0 72 36]", text);
    }

    // ------------------------------------------------------------------
    // Codecs da Microsoft Store (só quando instalados neste PC)
    // ------------------------------------------------------------------

    [Fact]
    public void Codecs_SempreIncluemOsDoWindows_ENuncaOJpegXl()
    {
        IReadOnlySet<string> readable = WicCodecs.Readable;

        Assert.All(ConversionFormats.BuiltInWicReadable, ext => Assert.Contains(ext, readable));
        Assert.DoesNotContain(".jxl", readable);
    }

    private void AssertStoreFixtureConverts(string ext, string base64)
    {
        string source = _temp.Combine("fixture" + ext);
        File.WriteAllBytes(source, Convert.FromBase64String(base64));
        string destination = _temp.Combine("fixture.png");

        ImageConverter.Convert(source, "png", destination);

        BitmapFrame result = Decode(destination);
        Assert.Equal((16, 8), (result.PixelWidth, result.PixelHeight));
        AssertNear(PixelAt(result, 2, 4), Red, tolerance: 24);
        AssertNear(PixelAt(result, 13, 4), Blue, tolerance: 24);
    }

    [RequiresImageDecoderFact(".webp")]
    public void Webp_ConverteParaPngComAsCoresCertas() => AssertStoreFixtureConverts(".webp", StoreImageFixtures.Webp);

    [RequiresImageDecoderFact(".avif")]
    public void Avif_ConverteParaPngComAsCoresCertas() => AssertStoreFixtureConverts(".avif", StoreImageFixtures.Avif);

    [RequiresHeicEncoderFact]
    public void Heic_IdaEVolta_QuandoOCodificadorFunciona()
    {
        string source = HalfRedHalfBluePng();
        string heic = _temp.Combine("ida.heic");
        ImageConverter.Convert(source, "heic", heic);
        string back = _temp.Combine("volta.png");
        ImageConverter.Convert(heic, "png", back);

        BitmapFrame result = Decode(back);
        Assert.Equal((40, 20), (result.PixelWidth, result.PixelHeight));
        AssertNear(PixelAt(result, 5, 10), Red, tolerance: 40);
        AssertNear(PixelAt(result, 35, 10), Blue, tolerance: 40);
    }

    // ------------------------------------------------------------------
    // Pelo serviço (rota, validação e progresso)
    // ------------------------------------------------------------------

    private sealed class CollectingProgress : IProgress<ProgressReport>
    {
        public List<ProgressReport> Reports { get; } = new();
        public void Report(ProgressReport value) { lock (Reports) Reports.Add(value); }
    }

    [Fact]
    public async Task ConvertAsync_PngParaPdf_PeloWindows_TerminaEm100()
    {
        string source = HalfRedHalfBluePng();
        string destination = _temp.Combine("metade.pdf");
        var progress = new CollectingProgress();

        await ConversionService.ConvertAsync(source, "PDF", destination, progress);

        ValidatePdfStructure(File.ReadAllBytes(destination));
        Assert.Equal(100.0, progress.Reports[^1].Percentage);
    }

    [Fact]
    public async Task ConvertAsync_SemCaminhoPossivel_FalhaSemCriarArquivo()
    {
        string source = _temp.CreateFile("documento.pdf");
        string destination = _temp.Combine("documento.png");

        await Assert.ThrowsAsync<NotSupportedException>(() => ConversionService.ConvertAsync(source, "png", destination, null));
        Assert.False(File.Exists(destination));
    }
}
