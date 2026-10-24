// ============================================================================
// RadialFlow - Tema da roda (claro/escuro + cor de destaque do Windows)
// ----------------------------------------------------------------------------
// Diretrizes seguidas (Fluent / Windows 11):
//   * material translúcido só em superfícies transitórias, como esta roda;
//   * respeitar o modo claro/escuro do sistema;
//   * usar a cor de destaque do usuário para o estado ativo, ajustada para ter
//     contraste suficiente em cada modo;
//   * texto sempre legível: a cor da tinta sobre o destaque é escolhida por
//     contraste WCAG (branco ou quase-preto), nunca fixa.
//
// Tudo aqui é puro (Create/ColorMath) e testado; só FromSystem lê o registro.
// ============================================================================

using System;
using System.Windows.Media;
using Microsoft.Win32;

namespace RadialFlow.UI;

internal sealed record RadialTheme(
    bool IsLight,
    Color Tint,            // cobertura sobre o fundo desfocado
    Color SegmentRest,     // véu da fatia em repouso
    Color BorderTop,       // contorno: realce no alto...
    Color BorderBottom,    // ...e sombra embaixo
    Color Ink,             // ícone e rótulo em repouso
    Color InkMuted,        // chevron
    Color HoverTop,        // destaque (gradiente)
    Color HoverBottom,
    Color HoverInk,        // ícone e rótulo sobre o destaque
    Color HoverBorder,
    Color Glow,            // brilho ao redor da fatia ativa
    Color HubFill,
    Color HubBorder,
    Color HubInk,
    Color PillFill,
    Color PillInk,
    Color DwellArc)
{
    private static readonly Color DefaultAccent = Color.FromRgb(0x3B, 0x82, 0xF6);

    /// <summary>Opções de cor de destaque oferecidas nas Configurações: chave gravada, nome exibido, cor (nula = a do Windows).</summary>
    internal static readonly (string Key, string Label, Color? Color)[] AccentChoices =
    [
        ("Blue",    "Azul",           Color.FromRgb(0x3B, 0x82, 0xF6)),
        ("Violet",  "Violeta",        Color.FromRgb(0x8B, 0x5C, 0xF6)),
        ("Teal",    "Verde-água",     Color.FromRgb(0x14, 0xB8, 0xA6)),
        ("Green",   "Verde",          Color.FromRgb(0x22, 0xC5, 0x5E)),
        ("Orange",  "Laranja",        Color.FromRgb(0xF9, 0x73, 0x16)),
        ("Pink",    "Rosa",           Color.FromRgb(0xEC, 0x48, 0x99)),
        ("Windows", "Cor do Windows", null),
    ];

    /// <summary>
    /// Cor para o nome gravado nas configurações. Nome desconhecido ou vazio = azul padrão;
    /// "Windows" lê a cor de destaque do sistema.
    /// </summary>
    internal static Color ResolveAccent(string? key)
    {
        foreach (var (name, _, color) in AccentChoices)
        {
            if (!string.Equals(name, key?.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            return color ?? ReadAccent();
        }

        return DefaultAccent;
    }

    public static RadialTheme FromSystem() => Create(ReadLightTheme(), ReadAccent());

    public static RadialTheme Create(bool light, Color accent)
    {
        (double h, double s, double l) = ColorMath.ToHsl(accent);

        // Destaque ajustado ao modo: vivo o bastante, nem apagado no escuro nem "lavado" no claro.
        // Acentos neutros (cinza/preto/branco do sistema) continuam neutros em vez de virarem vermelho.
        if (s >= 0.08) s = Math.Clamp(s, 0.55, 0.92);
        l = light ? Math.Clamp(l, 0.42, 0.54) : Math.Clamp(l, 0.52, 0.64);

        HoverStyle hover = ResolveHover(h, s, l);
        l = hover.Lightness;
        Color hoverTop = hover.Top;
        Color hoverBottom = hover.Bottom;
        Color hoverInk = hover.Ink;
        Color baseAccent = ColorMath.FromHsl(h, s, l);

        if (light)
        {
            return new RadialTheme(
                IsLight: true,
                Tint: Color.FromArgb(0xC8, 0xF4, 0xF6, 0xFA),
                SegmentRest: Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
                BorderTop: Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF),
                BorderBottom: Color.FromArgb(0x26, 0x1B, 0x22, 0x30),
                Ink: Color.FromRgb(0x1B, 0x22, 0x30),
                InkMuted: Color.FromRgb(0x5B, 0x65, 0x78),
                HoverTop: hoverTop,
                HoverBottom: hoverBottom,
                HoverInk: hoverInk,
                HoverBorder: Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF),
                Glow: Color.FromArgb(0xFF, baseAccent.R, baseAccent.G, baseAccent.B),
                HubFill: Color.FromArgb(0xEC, 0xFF, 0xFF, 0xFF),
                HubBorder: Color.FromArgb(0x30, 0x1B, 0x22, 0x30),
                HubInk: Color.FromRgb(0x1B, 0x22, 0x30),
                PillFill: Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF),
                PillInk: Color.FromRgb(0x1B, 0x22, 0x30),
                DwellArc: ColorMath.FromHsl(h, s, Math.Max(l - 0.04, 0.3)));
        }

        return new RadialTheme(
            IsLight: false,
            Tint: Color.FromArgb(0xBC, 0x12, 0x16, 0x20),
            SegmentRest: Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF),
            BorderTop: Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF),
            BorderBottom: Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF),
            Ink: Color.FromRgb(0xF3, 0xF6, 0xFB),
            InkMuted: Color.FromRgb(0xA9, 0xB3, 0xC6),
            HoverTop: hoverTop,
            HoverBottom: hoverBottom,
            HoverInk: hoverInk,
            HoverBorder: Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF),
            Glow: Color.FromArgb(0xFF, baseAccent.R, baseAccent.G, baseAccent.B),
            HubFill: Color.FromArgb(0xE8, 0x10, 0x14, 0x1E),
            HubBorder: Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF),
            HubInk: Color.FromRgb(0xF3, 0xF6, 0xFB),
            PillFill: Color.FromArgb(0xE8, 0x14, 0x19, 0x26),
            PillInk: Color.FromRgb(0xF3, 0xF6, 0xFB),
            DwellArc: ColorMath.FromHsl(h, s, Math.Min(l + 0.12, 0.8)));
    }

    /// <summary>Contraste mínimo do texto sobre o destaque (WCAG AA para texto normal).</summary>
    internal const double MinTextContrast = 4.5;

    internal readonly record struct HoverStyle(Color Top, Color Bottom, Color Ink, double Lightness);

    /// <summary>
    /// Escolhe o gradiente do destaque e a cor do texto. Prefere TEXTO BRANCO (mais elegante): escurece o
    /// destaque, até um piso, até o branco atingir <see cref="MinTextContrast"/> no trecho MAIS CLARO do
    /// gradiente. Se o matiz for claro demais para isso (amarelo, ciano), usa texto escuro e CLAREIA o destaque
    /// até o escuro atingir o contraste no trecho mais escuro. Nos dois casos o contraste mínimo é garantido.
    /// </summary>
    internal static HoverStyle ResolveHover(double h, double s, double requestedLightness)
    {
        static (Color Top, Color Bottom) Stops(double h, double s, double l) =>
            (ColorMath.FromHsl(h, s, Math.Min(l + 0.07, 0.92)), ColorMath.FromHsl(h, s, Math.Max(l - 0.06, 0.08)));

        // A) texto branco: do pedido para baixo, o primeiro tom que atende no trecho mais claro.
        for (double l = requestedLightness; l >= 0.32; l -= 0.01)
        {
            var (top, bottom) = Stops(h, s, l);
            if (ColorMath.Contrast(Colors.White, top) >= MinTextContrast)
                return new HoverStyle(top, bottom, Colors.White, l);
        }

        // B) texto escuro: do pedido para cima, o primeiro tom que atende no trecho mais escuro.
        for (double l = requestedLightness; l <= 0.85; l += 0.01)
        {
            var (top, bottom) = Stops(h, s, l);
            if (ColorMath.Contrast(ColorMath.DarkInk, bottom) >= MinTextContrast)
                return new HoverStyle(top, bottom, ColorMath.DarkInk, l);
        }

        var (lastTop, lastBottom) = Stops(h, s, 0.85);
        return new HoverStyle(lastTop, lastBottom, ColorMath.DarkInk, 0.85);
    }

    // ------------------------------------------------------------------
    // Leitura do sistema (a única parte não pura)
    // ------------------------------------------------------------------

    /// <summary>true se a BARRA DE TAREFAS usa o tema claro (HKCU\...\Themes\Personalize\SystemUsesLightTheme = 1). Pode diferir do tema dos aplicativos.</summary>
    internal static bool ReadTaskbarIsLight()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;   // escuro: o mais comum
        }
    }

    /// <summary>true se os aplicativos usam o tema claro (HKCU\...\Themes\Personalize\AppsUseLightTheme = 1).</summary>
    internal static bool ReadLightTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;   // escuro: combina com a maioria dos temas e com a roda original
        }
    }

    /// <summary>Cor de destaque (HKCU\Software\Microsoft\Windows\DWM\AccentColor, formato ABGR); azul padrão se ausente.</summary>
    internal static Color ReadAccent()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                uint value = unchecked((uint)abgr);
                return Color.FromRgb((byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF));
            }
        }
        catch
        {
            // sem acesso ao registro: usa o padrão
        }

        return Color.FromRgb(0x00, 0x78, 0xD4);   // azul clássico do Windows quando o registro não informa
    }
}

/// <summary>Matemática de cor: HSL, luminância e contraste WCAG 2.x.</summary>
internal static class ColorMath
{
    internal static readonly Color DarkInk = Color.FromRgb(0x10, 0x14, 0x1C);
    private static readonly Color NearBlack = DarkInk;

    public static (double H, double S, double L) ToHsl(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2;
        if (max - min < 1e-9) return (0, 0, l);

        double d = max - min;
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0)
                 : max == g ? (b - r) / d + 2
                 : (r - g) / d + 4;
        return (h * 60, s, l);
    }

    public static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360 / 360.0;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);

        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;

        static double Channel(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        return Color.FromRgb(
            (byte)Math.Round(Channel(p, q, h + 1.0 / 3) * 255),
            (byte)Math.Round(Channel(p, q, h) * 255),
            (byte)Math.Round(Channel(p, q, h - 1.0 / 3) * 255));
    }

    /// <summary>Luminância relativa (WCAG), 0 = preto, 1 = branco.</summary>
    public static double Luminance(Color c)
    {
        static double Linear(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    /// <summary>Razão de contraste WCAG entre duas cores opacas (1 a 21).</summary>
    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// Ajusta a LUMINOSIDADE de <paramref name="color"/> (mantendo matiz e saturação) até ter pelo menos
    /// <paramref name="minContrast"/> contra <paramref name="background"/>. Escurece sobre fundo claro e clareia
    /// sobre fundo escuro. Se nem o extremo atingir, devolve o extremo (preto ou branco).
    /// </summary>
    public static Color EnsureContrast(Color color, Color background, double minContrast)
    {
        if (Contrast(color, background) >= minContrast) return color;

        var (h, s, l) = ToHsl(color);
        bool darken = Luminance(background) > 0.5;
        double step = darken ? -0.01 : 0.01;

        for (double lightness = l; lightness is >= 0 and <= 1; lightness += step)
        {
            Color candidate = FromHsl(h, s, lightness);
            if (Contrast(candidate, background) >= minContrast) return candidate;
        }

        return darken ? Colors.Black : Colors.White;
    }

    /// <summary>Compõe uma cor (com alfa) sobre um fundo opaco.</summary>
    public static Color Over(Color top, Color background)
    {
        double a = top.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Round(top.R * a + background.R * (1 - a)),
            (byte)Math.Round(top.G * a + background.G * (1 - a)),
            (byte)Math.Round(top.B * a + background.B * (1 - a)));
    }

    /// <summary>Branco ou quase-preto: o de maior contraste no PIOR dos fundos informados.</summary>
    public static Color PickInk(params Color[] backgrounds)
    {
        double whiteWorst = double.MaxValue, blackWorst = double.MaxValue;
        foreach (Color background in backgrounds)
        {
            whiteWorst = Math.Min(whiteWorst, Contrast(Colors.White, background));
            blackWorst = Math.Min(blackWorst, Contrast(NearBlack, background));
        }

        return whiteWorst >= blackWorst ? Colors.White : NearBlack;
    }
}
