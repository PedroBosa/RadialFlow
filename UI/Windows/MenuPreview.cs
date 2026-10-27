// ============================================================================
// RadialFlow - Prévia da roda na aba Menus
// ----------------------------------------------------------------------------
// Desenha no próprio formulário como o menu do perfil ficaria: fatias
// automáticas em repouso, substituições e extras em destaque. Usa a mesma
// geometria arredondada da roda real, mas é só um Canvas: nada abre, nada
// captura o mouse e nenhuma janela topmost é criada.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RadialFlow.Context;
using RadialFlow.Infrastructure;
using RadialFlow.UI;

namespace RadialFlow.UI;

internal sealed class MenuPreview : Canvas
{
    internal const double PreviewSize = 280;

    private const double Center = PreviewSize / 2;
    private const double RingInner = 48;
    private const double RingOuter = 132;
    private const double Gap = 3;
    private const double Corner = 10;
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    private readonly List<CustomMergeEntry> _current = new();

    internal IReadOnlyList<CustomMergeEntry> Current => _current;

    public MenuPreview()
    {
        Width = PreviewSize;
        Height = PreviewSize;
        ClipToBounds = true;
        IsHitTestVisible = false;
    }

    internal void Show(IReadOnlyList<CustomMergeEntry> entries)
    {
        Children.Clear();
        _current.Clear();
        _current.AddRange(entries);

        DrawHub();
        if (entries.Count == 0)
        {
            AddCenteredText(Loc.Get("Menus.PreviewEmptyRings"), 11.5, "TextDimBrush", 38);
            return;
        }

        int count = entries.Count;
        for (int i = 0; i < count; i++)
            AddSlice(entries[i], i, count);
    }

    private void DrawHub()
    {
        const double hubSize = 58;

        var hub = new Ellipse { Width = hubSize, Height = hubSize, StrokeThickness = 1 };
        hub.SetResourceReference(Shape.FillProperty, "CardBrush");
        hub.SetResourceReference(Shape.StrokeProperty, "CardBorderBrush");
        Canvas.SetLeft(hub, Center - hubSize / 2);
        Canvas.SetTop(hub, Center - hubSize / 2);
        Children.Add(hub);

        AddCenteredText(Loc.Get("Menus.PreviewHub"), 10, "TextDimBrush", 0);
    }

    private void AddSlice(CustomMergeEntry entry, int index, int count)
    {
        Geometry shape = RadialGeometry.BuildRoundedSlice(index, count, Center, Center, RingInner, RingOuter, Gap, Corner);

        var path = new Path
        {
            Data = shape,
            StrokeThickness = entry.Status == CustomMergeStatus.Automatic ? 1.1 : 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            Opacity = entry.Status == CustomMergeStatus.Extra ? 0.92 : 1.0,
        };
        path.SetResourceReference(Shape.FillProperty, entry.Status switch
        {
            CustomMergeStatus.Extra => "AccentBrush",
            CustomMergeStatus.Replacement => "AccentSoftBrush",
            _ => "CardBrush",
        });
        path.SetResourceReference(Shape.StrokeProperty, entry.Status switch
        {
            CustomMergeStatus.Automatic => "CardBorderBrush",
            _ => "AccentBrush",
        });
        Children.Add(path);

        var panel = new StackPanel { IsHitTestVisible = false };
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(entry.Slice.Glyph) ? "\uE734" : entry.Slice.Glyph,
            FontFamily = new FontFamily(IconFont),
            FontSize = 16,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = entry.Slice.Label,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 72,
            MaxHeight = 28,
            Margin = new Thickness(0, 1, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        // Mesmo sinal da roda real: a fatia abre um segundo anel.
        if (entry.Slice.SubSlices is { Count: >= 2 })
        {
            panel.Children.Add(new TextBlock
            {
                Text = "\uE70D",
                FontFamily = new FontFamily(IconFont),
                FontSize = 9,
                Opacity = 0.75,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0),
            });
        }

        string ink = entry.Status == CustomMergeStatus.Extra ? "AccentInkBrush" : "TextBrush";
        foreach (TextBlock text in panel.Children)
            text.SetResourceReference(TextBlock.ForegroundProperty, ink);

        if (entry.Status is CustomMergeStatus.Replacement or CustomMergeStatus.Extra)
        {
            var badge = new TextBlock
            {
                Text = entry.Status == CustomMergeStatus.Replacement ? "substitui" : "extra",
                FontSize = 8.5,
                Opacity = 0.85,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0),
            };
            badge.SetResourceReference(TextBlock.ForegroundProperty,
                entry.Status == CustomMergeStatus.Extra ? "AccentInkBrush" : "AccentTextBrush");
            panel.Children.Add(badge);
        }

        Point position = RadialGeometry.Polar(Center, Center, (RingInner + RingOuter) / 2, RadialGeometry.CenterAngle(index, count));
        panel.Measure(new Size(100, double.PositiveInfinity));
        Size desired = panel.DesiredSize;
        Canvas.SetLeft(panel, position.X - desired.Width / 2);
        Canvas.SetTop(panel, position.Y - desired.Height / 2);
        Children.Add(panel);
    }

    private void AddCenteredText(string text, double fontSize, string brush, double verticalOffset)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        block.Measure(new Size(PreviewSize, double.PositiveInfinity));
        Canvas.SetLeft(block, Center - block.DesiredSize.Width / 2);
        Canvas.SetTop(block, Center - block.DesiredSize.Height / 2 + verticalOffset);
        Children.Add(block);
    }
}
