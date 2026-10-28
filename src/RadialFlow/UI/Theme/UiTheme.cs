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

        static Color Over(Color veil, Color background)
        {
            double alpha = veil.A / 255.0;
            return Color.FromRgb(
                (byte)Math.Round(veil.R * alpha + background.R * (1 - alpha)),
                (byte)Math.Round(veil.G * alpha + background.G * (1 - alpha)),
                (byte)Math.Round(veil.B * alpha + background.B * (1 - alpha)));
        }

        Color surface = light ? Color.FromRgb(0xF6, 0xF7, 0xF9) : Color.FromRgb(0x0F, 0x11, 0x15);
        Color card = light ? Colors.White : Color.FromRgb(0x15, 0x18, 0x1D);
        Color cardTop = card;
        Color cardBorder = light ? Color.FromRgb(0xE4, 0xE6, 0xEA) : Color.FromRgb(0x25, 0x28, 0x2F);
        Color text = light ? Color.FromRgb(0x18, 0x1A, 0x1E) : Color.FromRgb(0xEC, 0xEE, 0xF1);
        Color textDim = light ? Color.FromRgb(0x5F, 0x64, 0x6D) : Color.FromRgb(0x9B, 0xA1, 0xAA);

        // O destaque como TEXTO (links, seleção): ajustado até ter contraste AA sobre o cartão.
        Color accentText = ColorMath.EnsureContrast(theme.Glow, card, RadialTheme.MinTextContrast);

        // Tons do botão de destaque: o gradiente do hover da roda (já garantido para o texto sobre ele).
        Color accent = ColorMath.FromHsl(ColorMath.ToHsl(theme.HoverTop).H, ColorMath.ToHsl(theme.HoverTop).S,
            (ColorMath.ToHsl(theme.HoverTop).L + ColorMath.ToHsl(theme.HoverBottom).L) / 2);

        // Seleção neutra: o acento fica reservado ao estado ativo, sem pintar a superfície inteira.
        Color selection = light ? Color.FromRgb(0xEC, 0xEE, 0xF2) : Color.FromRgb(0x1F, 0x22, 0x28);
        Color accentOnSelection = ColorMath.EnsureContrast(theme.Glow, selection, RadialTheme.MinTextContrast);

        // Vidro: quase opaco, só para não brigar com o conteúdo; o pior caso segue preto (claro) ou branco (escuro).
        Color glassSurface = Color.FromArgb(light ? (byte)0xF5 : (byte)0xEF, surface.R, surface.G, surface.B);
        Color glassWorst = Over(glassSurface, light ? Colors.Black : Colors.White);
        Color accentOnGlass = ColorMath.EnsureContrast(theme.Glow, glassWorst, RadialTheme.MinTextContrast);
        Color danger = light ? Color.FromRgb(0xC4, 0x2B, 0x38) : Color.FromRgb(0xFF, 0x7B, 0x86);
        Color warning = Color.FromRgb(0xF5, 0xA5, 0x24);
        Color dangerOnGlass = ColorMath.EnsureContrast(danger, glassWorst, RadialTheme.MinTextContrast);
        Color warningOnGlass = ColorMath.EnsureContrast(warning, glassWorst, RadialTheme.MinTextContrast);

        return new UiPalette(
            IsLight: light,
            Surface: surface,
            SurfaceAlt: light ? Color.FromRgb(0xF1, 0xF2, 0xF5) : Color.FromRgb(0x12, 0x14, 0x19),
            GlassSurface: glassSurface,
            Card: card,
            CardBorder: cardBorder,
            CardTop: cardTop,
            CardEdge: Colors.Transparent,
            Text: text,
            TextDim: textDim,
            Control: light ? Color.FromRgb(0xEF, 0xF1, 0xF4) : Color.FromRgb(0x1B, 0x1E, 0x24),
            ControlHover: light ? Color.FromRgb(0xE6, 0xE8, 0xEC) : Color.FromRgb(0x23, 0x26, 0x2D),
            ControlBorder: light ? Color.FromRgb(0xDC, 0xE0, 0xE5) : Color.FromRgb(0x2C, 0x30, 0x38),
            Track: light ? Color.FromRgb(0xDC, 0xE0, 0xE5) : Color.FromRgb(0x2C, 0x30, 0x38),
            Accent: accent,
            AccentHover: theme.HoverTop,
            AccentPressed: theme.HoverBottom,
            AccentInk: theme.HoverInk,
            AccentText: accentText,
            AccentSoft: selection,
            AccentOnSoft: accentOnSelection,
            AccentOnGlass: accentOnGlass,
            AccentLine: cardBorder,
            AccentHalo: Colors.Transparent,
            AccentGlow: Colors.Transparent,
            Gloss: Colors.Transparent,
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
