// ============================================================================
// RadialFlow - Captura do fundo sob a roda (para o efeito de vidro fosco)
// ----------------------------------------------------------------------------
// WPF não consegue desfocar o que está ATRÁS de uma janela com transparência por
// pixel (DwmEnableBlurBehindWindow não desfoca desde o Windows 8, e o acrílico do
// sistema exige janela retangular). Então: copia a região da tela ANTES de a roda
// aparecer e a roda a desfoca e tinge por conta própria. Fica nítido, com bordas
// suaves e no formato do anel.
//
// Thread-safe (usa só GDI): chamada em segundo plano, em paralelo com a detecção
// de contexto. Qualquer falha devolve null e a roda usa uma tinta mais opaca.
// ============================================================================

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using RadialFlow.Infrastructure;

namespace RadialFlow.UI;

internal static class ScreenSnapshot
{
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;   // inclui janelas em camadas (translúcidas) de outros apps
    private const int MaxSide = 8192;

    /// <summary>
    /// Copia o quadrado centrado em (centerX, centerY), em pixels físicos da tela.
    /// </summary>
    /// <param name="sizePx">Lado do quadrado em pixels (limitado a 8192).</param>
    /// <returns>Imagem congelada (utilizável em qualquer thread) ou null se a captura falhar.</returns>
    public static BitmapSource? Capture(int centerX, int centerY, int sizePx)
    {
        if (sizePx <= 0 || sizePx > MaxSide) return null;
        return CaptureRect(centerX - sizePx / 2, centerY - sizePx / 2, sizePx, sizePx);
    }

    /// <summary>Copia um retângulo da tela, em pixels físicos (canto superior esquerdo em left/top).</summary>
    /// <returns>Imagem congelada (utilizável em qualquer thread) ou null se a captura falhar.</returns>
    public static BitmapSource? CaptureRect(int left, int top, int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide) return null;

        IntPtr screen = IntPtr.Zero, memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;

            memory = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, width, height);
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero) return null;

            previous = SelectObject(memory, bitmap);

            if (!BitBlt(memory, 0, 0, width, height, screen, left, top, SRCCOPY | CAPTUREBLT)) return null;

            SelectObject(memory, previous);
            previous = IntPtr.Zero;

            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            AppLog.Warning("Snapshot", $"Captura falhou: {ex.Message}");
            return null;
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hDc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, int rasterOperation);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hDc);
}
