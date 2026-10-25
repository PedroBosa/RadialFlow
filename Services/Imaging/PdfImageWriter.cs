// ============================================================================
// RadialFlow - Imagem para PDF (sem ferramentas externas)
// ----------------------------------------------------------------------------
// Gera um PDF 1.4 de uma página, do tamanho da imagem (pela resolução dela;
// 96 dpi quando o arquivo não informa uma razoável). Fotos entram como JPEG
// (DCTDecode); prints, desenhos e capturas entram sem perda (FlateDecode),
// para o texto continuar nítido. Transparência vira fundo branco.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RadialFlow.Services;

internal static class PdfImageWriter
{
    /// <summary>Maior lado de página permitido pelo PDF (200 polegadas).</summary>
    private const double MaxPagePoints = 14_400;

    /// <param name="image">Imagem já orientada; pode ter transparência.</param>
    /// <param name="photo">true: JPEG (menor); false: sem perda.</param>
    internal static byte[] Build(BitmapSource image, bool photo)
    {
        ArgumentNullException.ThrowIfNull(image);

        BitmapSource opaque = ImageConverter.FlattenOnWhite(image);
        int width = opaque.PixelWidth, height = opaque.PixelHeight;

        byte[] data;
        string filter;
        if (photo)
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
            encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(opaque, PixelFormats.Bgr24, null, 0)));
            using var jpeg = new MemoryStream();
            encoder.Save(jpeg);
            data = jpeg.ToArray();
            filter = "/DCTDecode";
        }
        else
        {
            var rgb = new FormatConvertedBitmap(opaque, PixelFormats.Rgb24, null, 0);
            var raw = new byte[width * height * 3];
            rgb.CopyPixels(raw, width * 3, 0);

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(raw);
            data = compressed.ToArray();
            filter = "/FlateDecode";
        }

        double dpiX = SaneDpi(image.DpiX), dpiY = SaneDpi(image.DpiY);
        double pageWidth = width * 72.0 / dpiX, pageHeight = height * 72.0 / dpiY;
        double fit = Math.Min(1.0, MaxPagePoints / Math.Max(pageWidth, pageHeight));
        pageWidth *= fit;
        pageHeight *= fit;

        string w = Num(pageWidth), h = Num(pageHeight);
        byte[] content = Encoding.ASCII.GetBytes($"q {w} 0 0 {h} 0 0 cm /Im0 Do Q\n");

        using var pdf = new MemoryStream();
        var offsets = new List<long>();

        void Ascii(string text) => pdf.Write(Encoding.ASCII.GetBytes(text));
        void BeginObject(int number)
        {
            offsets.Add(pdf.Position);
            Ascii(string.Create(CultureInfo.InvariantCulture, $"{number} 0 obj\n"));
        }

        Ascii("%PDF-1.4\n");
        pdf.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);   // marca de arquivo binário

        BeginObject(1);
        Ascii("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        BeginObject(2);
        Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        BeginObject(3);
        Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");

        BeginObject(4);
        Ascii(string.Create(CultureInfo.InvariantCulture,
            $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter {filter} /Length {data.Length} >>\nstream\n"));
        pdf.Write(data);
        Ascii("\nendstream\nendobj\n");

        BeginObject(5);
        Ascii(string.Create(CultureInfo.InvariantCulture, $"<< /Length {content.Length} >>\nstream\n"));
        pdf.Write(content);
        Ascii("endstream\nendobj\n");

        BeginObject(6);
        Ascii("<< /Producer (RadialFlow) >>\nendobj\n");

        long xref = pdf.Position;
        Ascii(string.Create(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f\r\n"));
        foreach (long offset in offsets)
            Ascii(string.Create(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n\r\n"));
        Ascii(string.Create(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info 6 0 R >>\nstartxref\n{xref}\n%%EOF\n"));

        return pdf.ToArray();
    }

    private static double SaneDpi(double dpi) => double.IsFinite(dpi) && dpi is >= 50 and <= 1200 ? dpi : 96;

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
