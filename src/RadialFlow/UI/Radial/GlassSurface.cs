// ============================================================================
// RadialFlow - Vidro das janelas transitórias
// ----------------------------------------------------------------------------
// A captura acontece em SourceInitialized: o HWND já está posicionado, mas a
// janela ainda não foi renderizada (a captura nesse ponto é idêntica ao fundo).
// Depois do primeiro layout, recortamos a área do cartão e reaproveitamos a
// receita de desfoque da roda. Se a captura falhar, o sistema estiver em alto
// contraste ou a janela sair da tela, o cartão usa a superfície opaca.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RadialFlow.Infrastructure;

namespace RadialFlow.UI;

internal static class GlassSurface
{
    private const int MonitorDefaultToNearest = 2;
    private const int MaxCaptureSide = 8192;

    /// <summary>Captura do monitor e a origem usada para recortar a área da janela.</summary>
    internal readonly record struct Capture(BitmapSource Image, int Left, int Top);

    /// <summary>O vidro só é usado sem alto contraste e com a janela inteira dentro de um monitor.</summary>
    internal static bool IsUsable(bool highContrast, bool windowInsideMonitor) => !highContrast && windowInsideMonitor;

    /// <summary>Captura o monitor da janela antes de ela aparecer. Chamar na thread de UI, em SourceInitialized.</summary>
    internal static Capture? CaptureBehind(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out NativeRect windowRect)) return null;

        var center = new NativePoint
        {
            X = windowRect.Left + (windowRect.Right - windowRect.Left) / 2,
            Y = windowRect.Top + (windowRect.Bottom - windowRect.Top) / 2
        };

        IntPtr monitor = MonitorFromPoint(center, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return null;

        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return null;

        NativeRect screen = info.Monitor;
        bool inside = windowRect.Left >= screen.Left && windowRect.Top >= screen.Top &&
                      windowRect.Right <= screen.Right && windowRect.Bottom <= screen.Bottom;
        if (!IsUsable(SystemParameters.HighContrast, inside)) return null;

        int width = screen.Right - screen.Left;
        int height = screen.Bottom - screen.Top;
        if (width < 1 || height < 1 || width > MaxCaptureSide || height > MaxCaptureSide) return null;

        BitmapSource? image = ScreenSnapshot.CaptureRect(screen.Left, screen.Top, width, height);
        return image is null ? null : new Capture(image, screen.Left, screen.Top);
    }

    /// <summary>Recorta a captura na área do cartão, desfoca e aplica. Qualquer falha deixa o cartão opaco.</summary>
    internal static bool Apply(Border imageHost, Border tintHost, Capture capture)
    {
        try
        {
            ImageBrush? brush = CreateBrush(CropAndBlur(imageHost, capture));
            if (brush is null)
            {
                MakeOpaque(imageHost, tintHost);
                return false;
            }

            imageHost.Background = brush;
            tintHost.SetResourceReference(Border.BackgroundProperty, "GlassSurfaceBrush");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warning("Glass", $"Falha ao aplicar o vidro: {ex.Message}");
            MakeOpaque(imageHost, tintHost);
            return false;
        }
    }

    /// <summary>Pincel esticado para preencher o cartão; null sem imagem.</summary>
    internal static ImageBrush? CreateBrush(BitmapSource? image)
    {
        if (image is null) return null;

        var brush = new ImageBrush(image) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    internal static void MakeOpaque(Border imageHost, Border tintHost)
    {
        imageHost.Background = null;
        tintHost.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
    }

    /// <summary>
    /// Captura na criação da janela e aplica no primeiro layout. Depois de aplicado, mover a janela
    /// descarta o vidro (a captura ficaria desalinhada); o cartão passa a ser opaco.
    /// </summary>
    internal static void Attach(Window window, Border imageHost, Border tintHost)
    {
        Capture? capture = null;
        window.SourceInitialized += (_, _) => capture = CaptureBehind(window);

        bool finished = false;
        window.ContentRendered += (_, _) =>
        {
            if (finished) return;
            finished = true;

            if (SystemParameters.HighContrast)
            {
                MakeOpaque(imageHost, tintHost);
                return;
            }

            if (capture is not { } shot || !Apply(imageHost, tintHost, shot)) return;

            var at = new Point(window.Left, window.Top);
            window.LocationChanged += OnMoved;

            void OnMoved(object? sender, EventArgs e)
            {
                if (Math.Abs(window.Left - at.X) < 0.5 && Math.Abs(window.Top - at.Y) < 0.5) return;
                window.LocationChanged -= OnMoved;
                MakeOpaque(imageHost, tintHost);
            }
        };
    }

    private static BitmapSource? CropAndBlur(FrameworkElement host, Capture capture)
    {
        if (host.ActualWidth < 1 || host.ActualHeight < 1) return null;

        Point origin = host.PointToScreen(new Point(0, 0));
        DpiScale dpi = VisualTreeHelper.GetDpi(host);
        int x = (int)Math.Round(origin.X - capture.Left);
        int y = (int)Math.Round(origin.Y - capture.Top);
        int width = (int)Math.Round(host.ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Round(host.ActualHeight * dpi.DpiScaleY);

        if (width < 1 || height < 1 || x < 0 || y < 0 ||
            x + width > capture.Image.PixelWidth || y + height > capture.Image.PixelHeight) return null;

        var crop = new CroppedBitmap(capture.Image, new Int32Rect(x, y, width, height));
        crop.Freeze();
        return FrostedBackdrop.FromSnapshot(crop);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);
}
