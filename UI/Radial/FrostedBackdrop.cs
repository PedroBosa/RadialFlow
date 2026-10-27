// ============================================================================
// RadialFlow - Vidro fosco: operações de pixel puras + montagem do fundo
// ----------------------------------------------------------------------------
// Receita de "acrílico": reduz a captura da tela (4x), desfoca (3 passadas de
// caixa ≈ gaussiana) e realça a saturação. A roda desenha essa imagem esticada
// dentro de cada fatia e cobre com a tinta do tema. Reduzir primeiro torna o
// desfoque barato (alguns milissegundos) e o esticamento suave faz o resto.
//
// Tudo aqui é independente de WPF/UI (arrays BGRA32) e roda em segundo plano.
// ============================================================================

using System;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RadialFlow.Infrastructure;

namespace RadialFlow.UI;

internal static class ImageOps
{
    /// <summary>Reduz por média de blocos (factor x factor). Ignora o alfa da origem (capturas de tela vêm com alfa 0) e grava alfa 255.</summary>
    public static (byte[] Pixels, int Width, int Height) Downscale(byte[] bgra, int width, int height, int factor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(factor, 1);
        if (bgra.Length < width * height * 4) throw new ArgumentException("Buffer menor que width*height*4.", nameof(bgra));

        int w2 = Math.Max(1, width / factor), h2 = Math.Max(1, height / factor);
        var result = new byte[w2 * h2 * 4];

        for (int y = 0; y < h2; y++)
        {
            for (int x = 0; x < w2; x++)
            {
                int sumB = 0, sumG = 0, sumR = 0, count = 0;
                int y0 = y * factor, y1 = Math.Min(y0 + factor, height);
                int x0 = x * factor, x1 = Math.Min(x0 + factor, width);

                for (int sy = y0; sy < y1; sy++)
                {
                    int row = sy * width * 4;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int i = row + sx * 4;
                        sumB += bgra[i];
                        sumG += bgra[i + 1];
                        sumR += bgra[i + 2];
                        count++;
                    }
                }

                int o = (y * w2 + x) * 4;
                count = Math.Max(count, 1);
                result[o] = (byte)(sumB / count);
                result[o + 1] = (byte)(sumG / count);
                result[o + 2] = (byte)(sumR / count);
                result[o + 3] = 255;
            }
        }

        return (result, w2, h2);
    }

    /// <summary>
    /// Desfoque de caixa separável, no próprio buffer, com bordas replicadas (sem escurecer/clarear as pontas).
    /// Várias passadas aproximam um desfoque gaussiano.
    /// </summary>
    public static void BoxBlur(byte[] bgra, int width, int height, int radius, int passes = 3)
    {
        if (radius < 1 || passes < 1 || width < 1 || height < 1) return;

        var temp = new byte[Math.Max(width, height) * 3];
        for (int pass = 0; pass < passes; pass++)
        {
            for (int y = 0; y < height; y++) BlurLine(bgra, y * width * 4, 4, width, radius, temp);
            for (int x = 0; x < width; x++) BlurLine(bgra, x * 4, width * 4, height, radius, temp);
        }
    }

    /// <summary>Desfoca uma linha ou coluna (início, passo em bytes, comprimento) com janela deslizante.</summary>
    private static void BlurLine(byte[] data, int start, int stride, int length, int radius, byte[] temp)
    {
        int window = radius * 2 + 1;

        for (int channel = 0; channel < 3; channel++)
        {
            int sum = 0;
            for (int k = -radius; k <= radius; k++)
                sum += data[start + Math.Clamp(k, 0, length - 1) * stride + channel];

            for (int i = 0; i < length; i++)
            {
                temp[i * 3 + channel] = (byte)((sum + window / 2) / window);

                int leaving = Math.Clamp(i - radius, 0, length - 1);
                int entering = Math.Clamp(i + radius + 1, 0, length - 1);
                sum += data[start + entering * stride + channel] - data[start + leaving * stride + channel];
            }
        }

        for (int i = 0; i < length; i++)
        {
            int o = start + i * stride;
            data[o] = temp[i * 3];
            data[o + 1] = temp[i * 3 + 1];
            data[o + 2] = temp[i * 3 + 2];
        }
    }

    /// <summary>Realça (factor &gt; 1) ou reduz a saturação mantendo a luminância. Cinzas permanecem cinzas.</summary>
    public static void Saturate(byte[] bgra, double factor)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            double b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            double gray = 0.114 * b + 0.587 * g + 0.299 * r;

            bgra[i] = ToByte(gray + (b - gray) * factor);
            bgra[i + 1] = ToByte(gray + (g - gray) * factor);
            bgra[i + 2] = ToByte(gray + (r - gray) * factor);
        }
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}

internal static class FrostedBackdrop
{
    internal const int DownscaleFactor = 4;
    internal const int BlurRadius = 5;          // no tamanho reduzido ≈ 20 px na tela
    internal const double Saturation = 1.3;

    /// <summary>
    /// Transforma a captura da tela em uma imagem pequena, desfocada e congelada (pronta para virar um pincel).
    /// Chamar fora da thread de UI.
    /// </summary>
    /// <returns>A imagem, ou null se a captura for nula ou a conversão falhar.</returns>
    public static BitmapSource? FromSnapshot(BitmapSource? snapshot)
    {
        if (snapshot is null) return null;

        try
        {
            var converted = new FormatConvertedBitmap(snapshot, PixelFormats.Bgra32, null, 0);
            int width = converted.PixelWidth, height = converted.PixelHeight;
            var pixels = new byte[width * height * 4];
            converted.CopyPixels(pixels, width * 4, 0);

            (byte[] small, int w, int h) = ImageOps.Downscale(pixels, width, height, DownscaleFactor);
            ImageOps.BoxBlur(small, w, h, BlurRadius);
            ImageOps.Saturate(small, Saturation);

            BitmapSource result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, small, w * 4);
            result.Freeze();
            return result;
        }
        catch (Exception ex)
        {
            AppLog.Warning("Backdrop", $"Falha ao montar o vidro fosco: {ex.Message}");
            return null;
        }
    }

    /// <summary>Captura + desfoque, em uma chamada. Fora da thread de UI.</summary>
    public static BitmapSource? Create(int centerX, int centerY, int sizePx) =>
        FromSnapshot(ScreenSnapshot.Capture(centerX, centerY, sizePx));

    /// <summary>Captura + desfoque de um retângulo da tela (pixels físicos). Fora da thread de UI.</summary>
    public static BitmapSource? CreateRect(int left, int top, int width, int height) =>
        FromSnapshot(ScreenSnapshot.CaptureRect(left, top, width, height));
}
