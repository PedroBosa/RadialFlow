// ============================================================================
// RadialFlow - Gravação de arquivos .ico
// ----------------------------------------------------------------------------
// Usado pelo ícone do próprio app (AppIconRenderer) e pela conversão de imagem
// para ICO. Abaixo de 256 px cada quadro é um DIB 32 bpp (o formato clássico,
// aceito por qualquer ferramenta); em 256 px é PNG (padrão desde o Vista).
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RadialFlow.Services;

internal static class IcoFile
{
    /// <summary>Monta um .ico com um quadro por imagem. Cada imagem precisa ser quadrada, de 1 a 256 px.</summary>
    internal static byte[] Build(IReadOnlyList<BitmapSource> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0) throw new ArgumentOutOfRangeException(nameof(images), "Pelo menos uma imagem.");
        if (images.Any(i => i.PixelWidth != i.PixelHeight || i.PixelWidth is < 1 or > 256))
            throw new ArgumentOutOfRangeException(nameof(images), "Imagens quadradas de 1 a 256 px.");

        var frames = images.Select(image =>
        {
            BitmapSource bgra = ToBgra32(image);
            return (Size: bgra.PixelWidth, Data: bgra.PixelWidth >= 256 ? ToPng(bgra) : ToDib(bgra));
        }).ToList();

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);                  // reservado
        writer.Write((ushort)1);                  // tipo: ícone
        writer.Write((ushort)frames.Count);

        int offset = 6 + 16 * frames.Count;
        foreach (var (size, data) in frames)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));   // 0 = 256
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);                // paleta
            writer.Write((byte)0);                // reservado
            writer.Write((ushort)1);              // planos
            writer.Write((ushort)32);             // bits por pixel
            writer.Write((uint)data.Length);
            writer.Write((uint)offset);
            offset += data.Length;
        }

        foreach (var (_, data) in frames) writer.Write(data);

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Pixels BGRA (alfa não pré-multiplicado), linha a linha, de cima para baixo.</summary>
    internal static byte[] GetPixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    internal static byte[] ToPng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource ToBgra32(BitmapSource image) =>
        image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);

    /// <summary>DIB de um quadro de .ico: cabeçalho (altura DOBRADA = cor + máscara), pixels de baixo para cima, máscara AND zerada.</summary>
    private static byte[] ToDib(BitmapSource bitmap)
    {
        int size = bitmap.PixelWidth;
        byte[] top = GetPixels(bitmap);
        int maskStride = (size + 31) / 32 * 4;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(40u);                        // BITMAPINFOHEADER
        writer.Write(size);
        writer.Write(size * 2);                   // cor + máscara
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0u);                         // BI_RGB
        writer.Write((uint)(size * size * 4 + maskStride * size));
        writer.Write(0); writer.Write(0);
        writer.Write(0u); writer.Write(0u);

        for (int y = size - 1; y >= 0; y--)       // de baixo para cima
            writer.Write(top, y * size * 4, size * 4);

        writer.Write(new byte[maskStride * size]); // alfa já está nos pixels: a máscara não recorta nada
        writer.Flush();
        return stream.ToArray();
    }
}
