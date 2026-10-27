// ============================================================================
// RadialFlow - Tema das janelas (mesma identidade da roda)
// ----------------------------------------------------------------------------
// Todas as janelas (Configurações, Progresso, Conversão, diálogos) usam esta
// paleta: segue o claro/escuro do Windows e a cor de destaque escolhida para a
// roda. Os estilos de controles ficam em Theme/Controls.xaml e só usam
// DynamicResource das chaves definidas aqui, então trocar a cor atualiza a
// janela aberta na hora.
//
// UiPalette é puro (testado: contraste de texto sobre cada superfície).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RadialFlow.UI;

/// <summary>Cores da interface das janelas. Valores opacos, exceto onde o nome indica transparência.</summary>
internal sealed record UiPalette(
    bool IsLight,
    Color Surface,        // fundo da janela
    Color SurfaceAlt,     // cabeçalho e rodapé
    Color GlassSurface,   // véu sobre o fundo desfocado (janelas transitórias)
    Color Card,
    Color CardBorder,
    Color CardTop,        // topo do gradiente do cartão
    Color CardEdge,       // fio de luz na borda superior do cartão
    Color Text,
    Color TextDim,
    Color Control,        // botões secundários, campos
    Color ControlHover,
    Color ControlBorder,
    Color Track,          // trilho do slider, chave desligada
    Color Accent,
    Color AccentHover,
    Color AccentPressed,
    Color AccentInk,      // texto sobre o destaque
    Color AccentText,     // destaque usado COMO TEXTO (garante contraste sobre Card)
    Color AccentSoft,     // véu do destaque (item selecionado)
    Color AccentOnSoft,   // destaque como TEXTO sobre o véu (garante contraste sobre AccentSoft)
    Color AccentOnGlass,  // destaque como TEXTO sobre o vidro (pior caso)
    Color AccentLine,     // contorno suave do destaque
    Color AccentHalo,     // anel de foco/seleção
    Color AccentGlow,     // brilho do botão principal
    Color Gloss,          // realce interno branco translúcido
    Color Danger,
    Color DangerOnGlass,  // erro como TEXTO sobre o vidro (pior caso)
    Color Warning,
    Color WarningOnGlass, // aviso como TEXTO sobre o vidro (pior caso)
    Color WarningInk)
{
    public static UiPalette Create(RadialTheme theme)
    {
        bool light = theme.IsLight;

        static Color Alpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

        static Color Over(Color veil, Color background)
        {
            double alpha = veil.A / 255.0;
            return Color.FromRgb(
                (byte)Math.Round(veil.R * alpha + background.R * (1 - alpha)),
                (byte)Math.Round(veil.G * alpha + background.G * (1 - alpha)),
                (byte)Math.Round(veil.B * alpha + background.B * (1 - alpha)));
        }

        Color surface = light ? Color.FromRgb(0xF2, 0xF4, 0xF8) : Color.FromRgb(0x12, 0x15, 0x1D);
        Color card = light ? Colors.White : Color.FromRgb(0x1A, 0x1F, 0x2B);
        Color cardTop = light ? Colors.White : Color.FromRgb(0x20, 0x27, 0x37);

        // O destaque como TEXTO (valores dos sliders, links): ajustado até ter contraste AA sobre o cartão.
        Color accentText = ColorMath.EnsureContrast(theme.Glow, card, RadialTheme.MinTextContrast);

        // Tons do botão de destaque: o gradiente do hover da roda (já garantido para o texto sobre ele).
        Color accent = ColorMath.FromHsl(ColorMath.ToHsl(theme.HoverTop).H, ColorMath.ToHsl(theme.HoverTop).S,
            (ColorMath.ToHsl(theme.HoverTop).L + ColorMath.ToHsl(theme.HoverBottom).L) / 2);

        Color accentSoft = Color.FromArgb(light ? (byte)0x26 : (byte)0x3A, theme.Glow.R, theme.Glow.G, theme.Glow.B);

        // O véu fica mais claro no topo do cartão: é o pior caso para texto sobre ele.
        Color accentOnSoft = ColorMath.EnsureContrast(theme.Glow, Over(accentSoft, cardTop), RadialTheme.MinTextContrast);

        // Vidro: o fundo desfocado pode ser qualquer imagem; o pior caso é preto (claro) ou branco (escuro).
        // O véu claro é 0xEE para o texto secundário ainda passar AA sobre o pior caso.
        Color glassSurface = Color.FromArgb(light ? (byte)0xEE : (byte)0xE0, surface.R, surface.G, surface.B);
        Color glassWorst = Over(glassSurface, light ? Colors.Black : Colors.White);
        Color accentOnGlass = ColorMath.EnsureContrast(theme.Glow, glassWorst, RadialTheme.MinTextContrast);
        Color danger = light ? Color.FromRgb(0xC4, 0x2B, 0x38) : Color.FromRgb(0xFF, 0x7B, 0x86);
        Color warning = Color.FromRgb(0xF5, 0xA5, 0x24);
        Color dangerOnGlass = ColorMath.EnsureContrast(danger, glassWorst, RadialTheme.MinTextContrast);
        Color warningOnGlass = ColorMath.EnsureContrast(warning, glassWorst, RadialTheme.MinTextContrast);

        return new UiPalette(
            IsLight: light,
            Surface: surface,
            SurfaceAlt: light ? Color.FromRgb(0xE8, 0xEC, 0xF3) : Color.FromRgb(0x16, 0x1A, 0x24),
            GlassSurface: glassSurface,
            Card: card,
            CardBorder: light ? Color.FromRgb(0xDD, 0xE2, 0xEC) : Color.FromRgb(0x2A, 0x31, 0x42),
            CardTop: cardTop,
            CardEdge: Color.FromArgb(light ? (byte)0xF2 : (byte)0x2E, 0xFF, 0xFF, 0xFF),
            Text: light ? Color.FromRgb(0x1B, 0x22, 0x30) : Color.FromRgb(0xF3, 0xF6, 0xFB),
            TextDim: light ? Color.FromRgb(0x5B, 0x65, 0x78) : Color.FromRgb(0xA9, 0xB3, 0xC6),
            Control: light ? Color.FromRgb(0xEE, 0xF1, 0xF7) : Color.FromRgb(0x23, 0x29, 0x39),
            ControlHover: light ? Color.FromRgb(0xE2, 0xE7, 0xF0) : Color.FromRgb(0x2D, 0x35, 0x4A),
            ControlBorder: light ? Color.FromRgb(0xCF, 0xD6, 0xE3) : Color.FromRgb(0x3A, 0x43, 0x59),
            Track: light ? Color.FromRgb(0xCD, 0xD4, 0xE2) : Color.FromRgb(0x3A, 0x43, 0x59),
            Accent: accent,
            AccentHover: theme.HoverTop,
            AccentPressed: theme.HoverBottom,
            AccentInk: theme.HoverInk,
            AccentText: accentText,
            AccentSoft: accentSoft,
            AccentOnSoft: accentOnSoft,
            AccentOnGlass: accentOnGlass,
            AccentLine: Alpha(theme.Glow, light ? (byte)0x4D : (byte)0x61),
            AccentHalo: Alpha(theme.Glow, light ? (byte)0x1A : (byte)0x26),
            AccentGlow: Alpha(theme.Glow, light ? (byte)0x4D : (byte)0x8C),
            Gloss: Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF),
            Danger: danger,
            DangerOnGlass: dangerOnGlass,
            Warning: warning,
            WarningOnGlass: warningOnGlass,
            WarningInk: Color.FromRgb(0x1F, 0x16, 0x05));
    }

    /// <summary>Pares chave → cor. As chaves de pincel terminam em "Brush" nos dicionários.</summary>
    public IEnumerable<(string Key, Color Color)> Entries()
    {
        yield return ("Surface", Surface);
        yield return ("SurfaceAlt", SurfaceAlt);
        yield return ("GlassSurface", GlassSurface);
        yield return ("Card", Card);
        yield return ("CardBorder", CardBorder);
        yield return ("CardTop", CardTop);
        yield return ("CardEdge", CardEdge);
        yield return ("Text", Text);
        yield return ("TextDim", TextDim);
        yield return ("Control", Control);
        yield return ("ControlHover", ControlHover);
        yield return ("ControlBorder", ControlBorder);
        yield return ("Track", Track);
        yield return ("Accent", Accent);
        yield return ("AccentHover", AccentHover);
        yield return ("AccentPressed", AccentPressed);
        yield return ("AccentInk", AccentInk);
        yield return ("AccentText", AccentText);
        yield return ("AccentSoft", AccentSoft);
        yield return ("AccentOnSoft", AccentOnSoft);
        yield return ("AccentOnGlass", AccentOnGlass);
        yield return ("AccentLine", AccentLine);
        yield return ("AccentHalo", AccentHalo);
        yield return ("AccentGlow", AccentGlow);
        yield return ("Gloss", Gloss);
        yield return ("Danger", Danger);
        yield return ("DangerOnGlass", DangerOnGlass);
        yield return ("Warning", Warning);
        yield return ("WarningOnGlass", WarningOnGlass);
        yield return ("WarningInk", WarningInk);
    }
}

internal static class UiTheme
{
    private const string BrushDictionaryTag = "RadialFlow.Brushes";

    /// <summary>Pincéis compostos (gradientes) criados em FillBrushes e recriados no Refresh.</summary>
    internal static readonly string[] GradientBrushKeys =
    [
        "CardSurfaceBrush",
        "CardEdgeBrush",
        "AccentGradientBrush",
        "AccentFillBrush"
    ];

    /// <summary>Tema atual: claro/escuro do Windows + cor de destaque escolhida nas configurações.</summary>
    public static RadialTheme CurrentTheme()
    {
        string accent = Config.SettingsService.Current.Accent;
        return RadialTheme.Create(RadialTheme.ReadLightTheme(), RadialTheme.ResolveAccent(accent));
    }

    /// <summary>
    /// Instala estilos e cores na janela/elemento: mescla Theme/Controls.xaml e um dicionário de pincéis.
    /// Chamar ANTES de InitializeComponent (os recursos precisam existir quando o XAML é carregado).
    /// </summary>
    public static void Install(FrameworkElement target, RadialTheme? theme = null)
    {
        UiPalette palette = UiPalette.Create(theme ?? CurrentTheme());

        var brushes = new ResourceDictionary();
        FillBrushes(brushes, palette);
        brushes[BrushDictionaryTag] = true;

        target.Resources.MergedDictionaries.Add(brushes);
        target.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/RadialFlow;component/UI/Theme/Controls.xaml", UriKind.Relative)
        });
    }

    /// <summary>Troca as cores de uma janela já aberta (ex.: depois de salvar uma nova cor de destaque).</summary>
    public static void Refresh(FrameworkElement target, RadialTheme theme)
    {
        UiPalette palette = UiPalette.Create(theme);
        foreach (ResourceDictionary dictionary in target.Resources.MergedDictionaries)
        {
            if (!dictionary.Contains(BrushDictionaryTag)) continue;
            FillBrushes(dictionary, palette);
            return;
        }
    }

    private static void FillBrushes(ResourceDictionary dictionary, UiPalette palette)
    {
        foreach (var (key, color) in palette.Entries())
        {
            // Cores e pincéis: o XAML usa {DynamicResource XxxBrush} para pincéis e {DynamicResource XxxColor} em animações.
            dictionary[key + "Brush"] = new SolidColorBrush(color);
            dictionary[key + "Color"] = color;
        }

        dictionary["CardSurfaceBrush"] = Freeze(new LinearGradientBrush(palette.CardTop, palette.Card, 90));
        dictionary["CardEdgeBrush"] = Freeze(new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(palette.CardEdge, 0),
                new GradientStop(palette.CardBorder, 0.3),
                new GradientStop(palette.CardBorder, 1)
            }
        });
        dictionary["AccentGradientBrush"] = Freeze(new LinearGradientBrush(palette.AccentHover, palette.AccentPressed, 90));
        dictionary["AccentFillBrush"] = Freeze(new LinearGradientBrush(palette.AccentPressed, palette.AccentHover, 0));
    }

    private static T Freeze<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }

    // ------------------------------------------------------------------
    // Barra de título no mesmo tom da janela (Windows 11) — ignorado onde não existe
    // ------------------------------------------------------------------

    /// <summary>
    /// Título escuro/claro e, no Windows 11, cor da barra igual à do fundo da janela (visual contínuo).
    /// Qualquer falha é ignorada: é só acabamento.
    /// </summary>
    public static void StyleTitleBar(Window window, RadialTheme? theme = null)
    {
        void Apply(object? sender, EventArgs e)
        {
            try
            {
                UiPalette palette = UiPalette.Create(theme ?? CurrentTheme());
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                int dark = palette.IsLight ? 0 : 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

                int caption = ToColorRef(palette.SurfaceAlt);
                int text = ToColorRef(palette.Text);
                int border = ToColorRef(palette.CardBorder);
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
            }
            catch
            {
                // acabamento visual: sem efeito colateral se falhar
            }
        }

        window.SourceInitialized += Apply;
    }

    /// <summary>COLORREF = 0x00BBGGRR.</summary>
    internal static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
