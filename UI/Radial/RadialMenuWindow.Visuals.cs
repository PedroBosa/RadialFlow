// ============================================================================
// RadialFlow - Menu radial: desenho das fatias
// ----------------------------------------------------------------------------
// Cada fatia é um GRUPO que se move e escala junto:
//   1. vidro fosco  (captura da tela desfocada, recortada no formato da fatia)
//   2. tinta do tema (garante leitura sobre qualquer fundo)
//   3. véu + contorno com realce (profundidade)
//   4. camada de destaque (cor de destaque do Windows, com brilho) — aparece ao pairar
//   5. ícone + rótulo (+ chevron se houver submenu)
// Como o grupo inteiro recebe o "salto" (escala a partir do centro da roda), o
// ícone e o nome ACOMPANHAM o hover junto com a fatia.
//
// Fatias com sub-opções desenham um traço na borda externa durante a espera,
// completo exatamente quando o submenu abre (RadialMenuWindow.DwellMilliseconds).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace RadialFlow.UI;

public partial class RadialMenuWindow
{
    // ---- Geometria do anel (DIPs) ----
    internal const double SizeDip = 440;
    private const double Center = SizeDip / 2;
    private const double RingInner = 58;            // também é o raio da zona neutra central
    private const double RingOuter = 170;
    private const double SegmentGapPx = 3.2;        // fresta de largura constante entre fatias
    private const double SegmentCorner = 9;
    private const double HoverPop = 1.06;           // a fatia ativa (grupo inteiro) cresce 6% a partir do centro
    private const double IconPop = 1.12;            // o ícone cresce um pouco mais
    private const double DwellArcRadius = RingOuter * HoverPop + 8;
    private const double DwellArcThickness = 3.5;
    private const double HubRadius = 40;            // metade do CenterHub (80 x 80) no XAML
    internal const double BackDwellRadius = (HubRadius + RingInner) / 2;   // no meio do vão entre o miolo e as fatias

    /// <summary>Comprimento do traço do "voltar" em múltiplos da espessura (unidade do StrokeDashArray), com folga.</summary>
    private static readonly double BackDwellDash = 2 * Math.PI * BackDwellRadius / DwellArcThickness + 1;

    private static readonly FontFamily IconFont  = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly FontFamily LabelFont = new("Segoe UI Variable Text, Segoe UI");

    private sealed class SegmentVisual
    {
        public required Canvas Group { get; init; }
        public required ScaleTransform Pop { get; init; }
        public required Path HoverLayer { get; init; }
        public required DropShadowEffect Glow { get; init; }
        public required SolidColorBrush Ink { get; init; }
        public required ScaleTransform IconScale { get; init; }
        public required Path? DwellArc { get; init; }
    }

    private readonly List<SegmentVisual> _segments = new();
    private RadialTheme _theme = RadialTheme.Create(light: false, accent: Color.FromRgb(0x00, 0x78, 0xD4));
    private ImageBrush? _backdropBrush;

    /// <summary>Largura máxima do rótulo de uma fatia (corda no centro do anel, com folga). Pura e testada.</summary>
    internal static double LabelMaxWidth(int sliceCount)
    {
        double midRadius = (RingInner + RingOuter) / 2;
        double chord = 2 * midRadius * Math.Sin(Math.PI / Math.Max(sliceCount, 1));
        return Math.Clamp(chord - 16, 40, 92);
    }

    // ------------------------------------------------------------------
    // Tema e fundo
    // ------------------------------------------------------------------

    private void ApplyTheme(RadialTheme theme)
    {
        _theme = theme;

        HubBg.Fill = new SolidColorBrush(theme.HubFill);
        HubBg.Stroke = new SolidColorBrush(theme.HubBorder);
        IdleDot.Stroke = new SolidColorBrush(theme.HubInk);
        HubGlyph.Foreground = new SolidColorBrush(theme.HubInk);

        BackDwellArc.Stroke = new SolidColorBrush(theme.DwellArc);
        BackDwellGlow.Color = theme.DwellArc;

        HintPill.Background = new SolidColorBrush(theme.PillFill);
        HintPill.BorderBrush = new SolidColorBrush(theme.HubBorder);
        HintText.Foreground = new SolidColorBrush(theme.PillInk);
    }

    private void ApplyBackdrop(System.Windows.Media.Imaging.BitmapSource? backdrop)
    {
        if (backdrop is null)
        {
            _backdropBrush = null;
            return;
        }

        // Viewport absoluto: cada fatia mostra a SUA parte da imagem única (coordenadas do canvas).
        var brush = new ImageBrush(backdrop)
        {
            Stretch = Stretch.Fill,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, SizeDip, SizeDip),
            TileMode = TileMode.None
        };
        brush.Freeze();
        _backdropBrush = brush;
    }

    // ------------------------------------------------------------------
    // Construção
    // ------------------------------------------------------------------

    private void BuildSegments()
    {
        WheelCanvas.Children.Clear();
        _segments.Clear();
        _hover = -1;

        int n = _currentSlices.Count;
        double midRadius = (RingInner + RingOuter) / 2;
        double step = 360.0 / n;
        double labelWidth = LabelMaxWidth(n);
        RadialTheme t = _theme;

        // Sem fundo capturado, a tinta fica mais opaca para continuar legível.
        Color tint = _backdropBrush is null ? Color.FromArgb(Math.Max(t.Tint.A, (byte)0xE6), t.Tint.R, t.Tint.G, t.Tint.B) : t.Tint;

        var restBorder = new LinearGradientBrush(t.BorderTop, t.BorderBottom, 90);
        restBorder.Freeze();
        var hoverFill = new LinearGradientBrush(t.HoverTop, t.HoverBottom, 90);
        hoverFill.Freeze();
        var hoverBorder = new SolidColorBrush(t.HoverBorder);
        hoverBorder.Freeze();
        var tintBrush = new SolidColorBrush(tint);
        tintBrush.Freeze();
        var restVeil = new SolidColorBrush(t.SegmentRest);
        restVeil.Freeze();

        for (int i = 0; i < n; i++)
        {
            Geometry shape = RadialGeometry.BuildRoundedSlice(i, n, Center, Center, RingInner, RingOuter, SegmentGapPx, SegmentCorner);
            RadialSlice slice = _currentSlices[i];

            var pop = new ScaleTransform(1, 1, Center, Center);
            var group = new Canvas { Width = SizeDip, Height = SizeDip, RenderTransform = pop, IsHitTestVisible = false };

            if (_backdropBrush is not null)
                group.Children.Add(new Path { Data = shape, Fill = _backdropBrush });
            group.Children.Add(new Path { Data = shape, Fill = tintBrush });
            group.Children.Add(new Path { Data = shape, Fill = restVeil, Stroke = restBorder, StrokeThickness = 1.1, StrokeLineJoin = PenLineJoin.Round });

            var glow = new DropShadowEffect
            {
                Color = t.Glow, BlurRadius = 26, ShadowDepth = 0, Opacity = 0, RenderingBias = RenderingBias.Performance
            };
            var hoverLayer = new Path
            {
                Data = shape, Fill = hoverFill, Stroke = hoverBorder, StrokeThickness = 1.1,
                StrokeLineJoin = PenLineJoin.Round, Opacity = 0, Effect = glow
            };
            group.Children.Add(hoverLayer);

            var ink = new SolidColorBrush(t.Ink);
            var iconScale = new ScaleTransform(1, 1);
            StackPanel content = BuildContent(slice, ink, t.InkMuted, labelWidth, iconScale, out FrameworkElement icon);

            var position = RadialGeometry.Polar(Center, Center, midRadius, RadialGeometry.CenterAngle(i, n));
            content.Measure(new Size(labelWidth + 4, double.PositiveInfinity));
            Canvas.SetLeft(content, position.X - content.DesiredSize.Width / 2);
            Canvas.SetTop(content, position.Y - content.DesiredSize.Height / 2);
            group.Children.Add(content);   // dentro do grupo: acompanha o salto da fatia

            Path? arc = slice.SubSlices is { Count: >= 2 } ? BuildDwellArc(i, n, step, t.DwellArc) : null;

            WheelCanvas.Children.Add(group);
            if (arc is not null)
            {
                Panel.SetZIndex(arc, 100);
                WheelCanvas.Children.Add(arc);
            }

            _segments.Add(new SegmentVisual
            {
                Group = group, Pop = pop, HoverLayer = hoverLayer, Glow = glow, Ink = ink,
                IconScale = iconScale, DwellArc = arc
            });
        }
    }

    private static StackPanel BuildContent(RadialSlice slice, Brush ink, Color muted, double labelWidth,
        ScaleTransform iconScale, out FrameworkElement icon)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };

        var glyph = new TextBlock
        {
            Text = slice.Glyph, FontFamily = IconFont, FontSize = 22, Foreground = ink,
            HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = iconScale
        };
        icon = glyph;
        panel.Children.Add(glyph);

        panel.Children.Add(new TextBlock
        {
            Text = slice.Label, FontFamily = LabelFont, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ink,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = labelWidth, MaxHeight = 34, Margin = new Thickness(0, 4, 0, 0)
        });

        if (slice.SubSlices is { Count: >= 2 })
        {
            // Chevron: "há mais opções ao segurar". Acompanha a tinta (muda para a cor do destaque ao pairar).
            panel.Children.Add(new TextBlock
            {
                Text = "\uE70D", FontFamily = IconFont, FontSize = 9, Foreground = ink, Opacity = 0.75,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0)
            });
        }

        return panel;
    }

    /// <summary>Arco na borda externa da fatia; começa "não desenhado" e é completado por <see cref="StartDwellProgress"/>.</summary>
    private static Path BuildDwellArc(int index, int count, double stepDeg, Color color)
    {
        double mid = RadialGeometry.CenterAngle(index, count);
        double a0 = mid - stepDeg / 2 + 5;
        double a1 = mid + stepDeg / 2 - 5;

        var figure = new PathFigure { StartPoint = RadialGeometry.Polar(Center, Center, DwellArcRadius, a0), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(RadialGeometry.Polar(Center, Center, DwellArcRadius, a1),
            new Size(DwellArcRadius, DwellArcRadius), 0, a1 - a0 > 180, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();

        // StrokeDashArray é medido em múltiplos da espessura: padrão [traço, vão] maior que o arco.
        double arcLength = DwellArcRadius * (a1 - a0) * Math.PI / 180.0;
        double dash = arcLength / DwellArcThickness + 1;

        return new Path
        {
            Data = geometry, Stroke = new SolidColorBrush(color), StrokeThickness = DwellArcThickness,
            StrokeDashArray = new DoubleCollection { dash, dash }, StrokeDashOffset = dash,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Effect = new DropShadowEffect { Color = color, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.8, RenderingBias = RenderingBias.Performance },
            Visibility = Visibility.Collapsed, IsHitTestVisible = false, Tag = dash
        };
    }

    // ------------------------------------------------------------------
    // Animações
    // ------------------------------------------------------------------

    private void AnimateSegment(SegmentVisual s, bool on)
    {
        var d = TimeSpan.FromMilliseconds(110);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        s.HoverLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, d));
        s.Glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(on ? 0.65 : 0, d));
        s.Ink.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(on ? _theme.HoverInk : _theme.Ink, d));

        // O grupo inteiro salta (forma + ícone + rótulo) e o ícone cresce um pouco mais.
        var pop = new DoubleAnimation(on ? HoverPop : 1.0, d) { EasingFunction = ease };
        s.Pop.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        s.Pop.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

        var icon = new DoubleAnimation(on ? IconPop : 1.0, d) { EasingFunction = ease };
        s.IconScale.BeginAnimation(ScaleTransform.ScaleXProperty, icon);
        s.IconScale.BeginAnimation(ScaleTransform.ScaleYProperty, icon);

        Panel.SetZIndex(s.Group, on ? 10 : 0);
    }

    /// <summary>Desenha o traço da borda externa ao longo de <paramref name="duration"/>.</summary>
    private static void StartDwellProgress(SegmentVisual s, TimeSpan duration)
    {
        if (s.DwellArc is null) return;

        double dash = (double)s.DwellArc.Tag;
        s.DwellArc.Visibility = Visibility.Visible;
        s.DwellArc.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(dash, 0, duration));
    }

    private void HideAllDwellArcs()
    {
        foreach (SegmentVisual s in _segments)
        {
            if (s.DwellArc is null) continue;

            s.DwellArc.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            s.DwellArc.StrokeDashOffset = (double)s.DwellArc.Tag;
            s.DwellArc.Visibility = Visibility.Collapsed;
        }

        BackDwellArc.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        BackDwellArc.StrokeDashOffset = BackDwellDash;
        BackDwellArc.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Círculo em volta do miolo, começando no topo e seguindo no sentido horário (como o traço das fatias).
    /// Duas meias-voltas: um único ArcSegment não desenha um círculo inteiro.
    /// </summary>
    private static PathGeometry BuildBackDwellGeometry()
    {
        var top = RadialGeometry.Polar(Center, Center, BackDwellRadius, -90);
        var bottom = RadialGeometry.Polar(Center, Center, BackDwellRadius, 90);
        var size = new Size(BackDwellRadius, BackDwellRadius);

        var figure = new PathFigure { StartPoint = top, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(bottom, size, 0, false, SweepDirection.Clockwise, true));
        figure.Segments.Add(new ArcSegment(top, size, 0, false, SweepDirection.Clockwise, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private static readonly PathGeometry BackDwellGeometry = BuildBackDwellGeometry();

    /// <summary>Desenha o círculo do "voltar" em volta do miolo ao longo de <paramref name="duration"/>.</summary>
    private void StartBackDwellProgress(TimeSpan duration)
    {
        if (BackDwellArc.Data is null)
        {
            BackDwellArc.Data = BackDwellGeometry;
            BackDwellArc.StrokeThickness = DwellArcThickness;
            BackDwellArc.StrokeDashArray = new DoubleCollection { BackDwellDash, BackDwellDash };
        }

        BackDwellArc.Visibility = Visibility.Visible;
        BackDwellArc.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(BackDwellDash, 0, duration));
    }

    /// <summary>true enquanto o círculo do "voltar" está sendo desenhado (testes).</summary>
    internal bool IsBackDwellVisible => BackDwellArc.Visibility == Visibility.Visible;
}
