// ============================================================================
// RadialFlow - Conversão de imagem pelo Windows (WIC), sem ferramentas externas
// ----------------------------------------------------------------------------
// Lê qualquer formato que o Windows decodifica (inclusive WebP, HEIC, AVIF e
// RAW com as extensões da Store) e grava PNG, JPG, BMP, GIF, TIFF, HEIC, ICO e
// PDF. Cuidados:
//   * orientação EXIF aplicada (fotos de celular não saem deitadas);
//   * JPG, BMP e PDF não têm transparência: o fundo vira branco (não preto);
//   * ICO com vários tamanhos, de 16 a 256 px, mantendo a proporção;
//   * PNG/TIFF de 16 bits continuam com 16 bits.
// Só o primeiro quadro é usado (GIF/WebP animados viram imagem parada).
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RadialFlow.Infrastructure;

namespace RadialFlow.Services;

internal static class ImageConverter
{
    /// <summary>Tamanhos do .ico gerado (só até o tamanho da imagem, mas sempre ao menos até 48 px).</summary>
    internal static readonly int[] IconSizes = [256, 128, 64, 48, 32, 24, 16];

    /// <summary>Converte <paramref name="sourcePath"/> para <paramref name="target"/> e grava em um arquivo NOVO.</summary>
    internal static void Convert(string sourcePath, string target, string destinationPath)
    {
        BitmapSource image = Load(sourcePath);
        byte[] output = Encode(image, target, ConversionFormats.IsPhoto(Path.GetExtension(sourcePath)));

        using var stream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(output);
    }

    /// <summary>Codifica a imagem já carregada (em memória: nada é gravado pela metade se falhar).</summary>
    internal static byte[] Encode(BitmapSource image, string target, bool photo)
    {
        switch (target)
        {
            case "ico":
                return IcoFile.Build(BuildIconFrames(image));

            case "pdf":
                return PdfImageWriter.Build(image, photo);
        }

        BitmapEncoder encoder = target switch
        {
            "png" => new PngBitmapEncoder(),
            "jpg" => new JpegBitmapEncoder { QualityLevel = 90 },
            "bmp" => new BmpBitmapEncoder(),
            "gif" => new GifBitmapEncoder(),
            "tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            "heic" => BitmapEncoder.Create(WicCodecs.HeifContainer),
            _ => throw new NotSupportedException(Loc.Format("ImageConverter.CannotWrite", target))
        };

        encoder.Frames.Add(BitmapFrame.Create(PrepareFor(image, target)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // ------------------------------------------------------------------
    // Leitura
    // ------------------------------------------------------------------

    /// <summary>Primeiro quadro, com a orientação EXIF aplicada. Congelado (pode trocar de thread).</summary>
    internal static BitmapSource Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        BitmapDecoder decoder;
        try
        {
            decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        }
        // ArgumentException: variante que o codec do Windows não decodifica (ex.: AVIF 4:4:4 sem perda).
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or COMException or ArgumentException)
        {
            throw new InvalidOperationException(Loc.Get("ImageConverter.CannotOpen"), ex);
        }

        if (decoder.Frames.Count == 0) throw new InvalidOperationException(Loc.Get("ImageConverter.NoFrames"));

        BitmapFrame frame = decoder.Frames[0];
        BitmapSource image = frame;

        // Decodificadores de terceiros (WebP, HEIF) podem informar um formato "Default": normaliza para BGRA.
        if (image.Format == PixelFormats.Default || image.Format.BitsPerPixel == 0)
            image = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);

        image = ApplyOrientation(image, ReadOrientation(frame));
        image.Freeze();
        return image;
    }

    /// <summary>Orientação EXIF (1 a 8); 1 quando ausente ou ilegível.</summary>
    internal static int ReadOrientation(BitmapFrame frame)
    {
        BitmapMetadata? metadata;
        try { metadata = frame.Metadata as BitmapMetadata; }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or COMException) { return 1; }
        if (metadata is null) return 1;

        foreach (string query in new[] { "System.Photo.Orientation", "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
        {
            try
            {
                if (metadata.GetQuery(query) is ushort value && value is >= 1 and <= 8) return value;
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or COMException)
            {
                // este formato não tem esse caminho de metadados: tenta o próximo
            }
        }

        return 1;
    }

    /// <summary>Gira/espelha conforme a orientação EXIF, para a imagem ficar como a câmera mostrou.</summary>
    internal static BitmapSource ApplyOrientation(BitmapSource image, int orientation)
    {
        Transform? transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(270) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(90) } },
            8 => new RotateTransform(270),
            _ => null
        };

        return transform is null ? image : new TransformedBitmap(image, transform);
    }

    // ------------------------------------------------------------------
    // Pixels por destino
    // ------------------------------------------------------------------

    private static BitmapSource PrepareFor(BitmapSource image, string target)
    {
        bool highDepth = image.Format == PixelFormats.Rgba64 || image.Format == PixelFormats.Rgb48 || image.Format == PixelFormats.Gray16;
        if (highDepth && target is "png" or "tiff") return image;   // não perde os 16 bits

        BitmapSource bgra = ToBgra32(image);
        bool alpha = HasTransparentPixels(bgra);

        return target switch
        {
            "jpg" or "bmp" => new FormatConvertedBitmap(FlattenOnWhite(bgra), PixelFormats.Bgr24, null, 0),
            _ when !alpha => new FormatConvertedBitmap(bgra, PixelFormats.Bgr24, null, 0),
            _ => bgra
        };
    }

    internal static BitmapSource ToBgra32(BitmapSource image)
    {
        if (image.Format == PixelFormats.Bgra32) return image;
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    internal static bool HasTransparentPixels(BitmapSource bgra)
    {
        byte[] pixels = IcoFile.GetPixels(ToBgra32(bgra));
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 255) return true;
        return false;
    }

    /// <summary>Compõe a imagem sobre branco, em pixels (sem depender de renderização WPF/STA).</summary>
    internal static BitmapSource FlattenOnWhite(BitmapSource source)
    {
        BitmapSource bgra = ToBgra32(source);
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

    // ------------------------------------------------------------------
    // ICO
    // ------------------------------------------------------------------

    internal static List<BitmapSource> BuildIconFrames(BitmapSource image)
    {
        int longest = Math.Max(image.PixelWidth, image.PixelHeight);
        int limit = Math.Max(longest, 48);
        return IconSizes.Where(size => size <= limit).Select(size => FitSquare(image, size)).ToList();
    }

    /// <summary>Encaixa a imagem num quadrado transparente de <paramref name="size"/> px, sem distorcer.</summary>
    internal static BitmapSource FitSquare(BitmapSource image, int size)
    {
        double scale = (double)size / Math.Max(image.PixelWidth, image.PixelHeight);
        int width = Math.Clamp((int)Math.Round(image.PixelWidth * scale), 1, size);
        int height = Math.Clamp((int)Math.Round(image.PixelHeight * scale), 1, size);

        byte[] resized = Resize(ToBgra32(image), width, height);
        var square = new byte[size * size * 4];
        int left = (size - width) / 2, top = (size - height) / 2;
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(resized, y * width * 4, square, ((top + y) * size + left) * 4, width * 4);

        var result = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, square, size * 4);
        result.Freeze();
        return result;
    }

    /// <summary>
    /// Redimensiona pela média da área coberta (boa qualidade ao reduzir), ponderada pelo alfa para as bordas
    /// transparentes não escurecerem. Ao ampliar, equivale ao vizinho mais próximo.
    /// </summary>
    internal static byte[] Resize(BitmapSource bgra, int width, int height)
    {
        int sw = bgra.PixelWidth, sh = bgra.PixelHeight;
        byte[] src = IcoFile.GetPixels(bgra);
        var dst = new byte[width * height * 4];
        double sx = (double)sw / width, sy = (double)sh / height;

        for (int y = 0; y < height; y++)
        {
            double y0 = y * sy, y1 = y0 + sy;
            int iy0 = (int)y0, iy1 = Math.Min(sh, (int)Math.Ceiling(y1));

            for (int x = 0; x < width; x++)
            {
                double x0 = x * sx, x1 = x0 + sx;
                int ix0 = (int)x0, ix1 = Math.Min(sw, (int)Math.Ceiling(x1));
                double b = 0, g = 0, r = 0, alpha = 0, area = 0;

                for (int iy = iy0; iy < iy1; iy++)
                {
                    double wy = Math.Min(y1, iy + 1) - Math.Max(y0, iy);
                    if (wy <= 0) continue;

                    for (int ix = ix0; ix < ix1; ix++)
                    {
                        double wx = Math.Min(x1, ix + 1) - Math.Max(x0, ix);
                        if (wx <= 0) continue;

                        double weight = wx * wy;
                        int i = (iy * sw + ix) * 4;
                        double wa = src[i + 3] * weight;
                        b += src[i] * wa;
                        g += src[i + 1] * wa;
                        r += src[i + 2] * wa;
                        alpha += wa;
                        area += weight;
                    }
                }

                int o = (y * width + x) * 4;
                if (alpha > 0)
                {
                    dst[o] = (byte)Math.Round(b / alpha);
                    dst[o + 1] = (byte)Math.Round(g / alpha);
                    dst[o + 2] = (byte)Math.Round(r / alpha);
                }
                dst[o + 3] = area > 0 ? (byte)Math.Round(alpha / area) : (byte)0;
            }
        }

        return dst;
    }
}
