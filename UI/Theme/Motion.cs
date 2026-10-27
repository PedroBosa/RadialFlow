// ============================================================================
// RadialFlow - Movimento das janelas
// ----------------------------------------------------------------------------
// A mesma transição curta da roda (110 ms, saída suave) para hover, pressão e
// entrada dos painéis transitórios. A entrada respeita "ClientAreaAnimation"
// (Acessibilidade do Windows); os micro-estados dos controles são curtos e não
// deslocam layout, então seguem sempre.
// ============================================================================

using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RadialFlow.UI;

internal static class Motion
{
    internal static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(110);

    internal static bool Enabled => SystemParameters.ClientAreaAnimation;

    /// <summary>Entrada dos painéis: fade + 98% → 100%, disparada no primeiro ContentRendered.</summary>
    internal static void AttachEntrance(Window window, FrameworkElement root, bool animate)
    {
        if (!animate) return;

        var scale = new ScaleTransform(0.98, 0.98);
        root.RenderTransformOrigin = new Point(0.5, 0.5);
        root.RenderTransform = scale;
        root.Opacity = 0;

        bool started = false;
        window.ContentRendered += (_, _) =>
        {
            if (started) return;
            started = true;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fade = new DoubleAnimation(1, Fast) { EasingFunction = ease };
            fade.Completed += (_, _) =>
            {
                root.BeginAnimation(UIElement.OpacityProperty, null);
                root.Opacity = 1;
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = 1;
                scale.ScaleY = 1;
            };
            root.BeginAnimation(UIElement.OpacityProperty, fade);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, Fast) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, Fast) { EasingFunction = ease });
        };
    }
}
