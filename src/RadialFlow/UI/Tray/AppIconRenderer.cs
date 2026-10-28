// ============================================================================
// RadialFlow - Ícone do aplicativo
// ----------------------------------------------------------------------------
// O ícone É o pie: um anel de fatias com cantos arredondados e fresta constante, uma
// delas (a "ativa") em azul. Desenhado com a MESMA geometria da roda real
// (RadialGeometry.BuildRoundedSlice), então é coerente por construção.
//
//   Tile  : azulejo escuro arredondado + anel claro. Executável, janelas, barra de tarefas.
//   Glyph : só o anel, sem azulejo. Bandeja do sistema (a cor do anel acompanha o tema
//           da barra de tarefas, claro ou escuro) e versão "pausado" em cinza.
//
// Detalhe por tamanho: abaixo de 28 px o anel tem 4 fatias (6 viram borrão) e a fresta
// nunca fica abaixo de 1 px (1,6 px abaixo de 28 px).
//
// Este arquivo também é compilado pelo gerador (tools/IconGenerator), que grava
// src/RadialFlow/Assets/RadialFlow.ico. Por isso não depende de nada além de WPF e de RadialModels.cs.
// Renderizar exige uma thread STA (RenderTargetBitmap).
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RadialFlow.Services;

namespace RadialFlow.UI;

internal enum IconVariant
{
    /// <summary>Azulejo escuro arredondado + anel. Para executável e janelas.</summary>
    Tile,

    /// <summary>Somente o anel (fundo transparente). Para a bandeja do sistema.</summary>
    Glyph
}

internal static class AppIconRenderer
{
    /// <summary>Tamanhos gravados no .ico: cobrem 100%–400% de DPI da bandeja, da barra de tarefas e do Explorer.</summary>
    internal static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    // ---- Paleta da marca ----
    internal static readonly Color BrandBlueTop = Color.FromRgb(0x6B, 0xA8, 0xFF);
    internal static readonly Color BrandBlueBottom = Color.FromRgb(0x25, 0x63, 0xEB);
    internal static readonly Color TileTop = Color.FromRgb(0x1D, 0x29, 0x48);
    internal static readonly Color TileBottom = Color.FromRgb(0x0C, 0x11, 0x20);
    internal static readonly Color RingOnTile = Color.FromRgb(0xE4, 0xEA, 0xF6);
    internal static readonly Color RingOnDarkBar = Color.FromRgb(0xF1, 0xF5, 0xFB);
    internal static readonly Color RingOnLightBar = Color.FromRgb(0x1F, 0x29, 0x37);

    /// <summary>Quantas fatias o anel tem neste tamanho.</summary>
    internal static int SliceCount(int size) => size < 28 ? 4 : 6;

    /// <summary>A fatia em azul: a do alto à direita (a de "Converter" na roda de arquivos).</summary>
    internal const int HighlightIndex = 1;

    // ------------------------------------------------------------------
    // Desenho
    // ------------------------------------------------------------------

    /// <param name="size">Lado em pixels (≥ 8).</param>
    /// <param name="paused">Anel todo em cinza (sem fatia azul): estado "pausado" da bandeja.</param>
    /// <param name="lightBackground">Variante Glyph: true quando a barra de tarefas é clara (anel escuro).</param>
    internal static DrawingVisual Draw(int size, IconVariant variant, bool paused = false, bool lightBackground = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 8);

        double s = size;
        double center = s / 2;
        bool tile = variant == IconVariant.Tile;

        // O anel ocupa mais espaço sem azulejo (e sem o "salto" da fatia ativa, que encostaria na borda).
        double outer = s * (tile ? 0.355 : 0.45);
        double inner = outer * 0.42;   // anel grosso: com buraco grande vira "boia"; o miolo abaixo completa a leitura de roda
        // Abaixo de 28 px a fresta de 1 px vira só um tracejado fraco depois do antialiasing: precisa de ~1,6 px.
        double gap = Math.Max(size < 28 ? 1.6 : 1.0, s * 0.04);
        double corner = Math.Max(0.6, s * (tile ? 0.034 : 0.04));
        int count = SliceCount(size);

        Color ring = tile ? RingOnTile : lightBackground ? RingOnLightBar : RingOnDarkBar;
        Brush restBrush = new SolidColorBrush(paused ? Color.FromArgb(0x8C, ring.R, ring.G, ring.B) : ring);
        Brush accentBrush = new LinearGradientBrush(BrandBlueTop, BrandBlueBottom, 90);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            if (tile)
            {
                double inset = s * 0.045;
                var rect = new Rect(inset, inset, s - inset * 2, s - inset * 2);
                double radius = s * 0.215;
                Brush tileBrush = new LinearGradientBrush(TileTop, TileBottom, 45);
                var border = new Pen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), Math.Max(1.0, s * 0.012));
                dc.DrawRoundedRectangle(tileBrush, border, rect, radius, radius);
            }

            for (int i = 0; i < count; i++)
            {
                Geometry slice = RadialGeometry.BuildRoundedSlice(i, count, center, center, inner, outer, gap, corner);
                bool lit = !paused && i == HighlightIndex;

                if (lit && tile && size >= 32)
                {
                    // A fatia ativa "salta" um pouco para fora, como na roda.
                    dc.PushTransform(new ScaleTransform(1.07, 1.07, center, center));
                    dc.DrawGeometry(accentBrush, null, slice);
                    dc.Pop();
                }
                else
                {
                    dc.DrawGeometry(lit ? accentBrush : restBrush, null, slice);
                }
            }

            // Miolo: o "hub" da roda. Sem ele o anel lê como "boia". Abaixo de 20 px não há pixels para ele.
            if (size >= 20)
            {
                double hub = inner * 0.46;
                dc.DrawEllipse(restBrush, null, new Point(center, center), hub, hub);
            }
        }

        return visual;
    }

    /// <summary>Renderiza em alfa NÃO pré-multiplicado (Bgra32), o formato de um .ico. Congelado.</summary>
    internal static BitmapSource Render(int size, IconVariant variant, bool paused = false, bool lightBackground = false)
    {
        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(Draw(size, variant, paused, lightBackground));

        var straight = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
        straight.Freeze();
        return straight;
    }

    /// <summary>Pixels BGRA (alfa não pré-multiplicado), linha a linha, de cima para baixo.</summary>
    internal static byte[] GetPixels(BitmapSource bitmap) => IcoFile.GetPixels(bitmap);

    internal static byte[] ToPng(BitmapSource bitmap) => IcoFile.ToPng(bitmap);

    // ------------------------------------------------------------------
    // Formato .ico
    // ------------------------------------------------------------------

    /// <summary>
    /// Monta um .ico com um quadro por tamanho. Abaixo de 256 px o quadro é um DIB 32 bpp (o formato clássico, aceito por
    /// qualquer ferramenta); em 256 px é PNG (o único que cabe com boa compressão e é o padrão desde o Vista).
    /// </summary>
    internal static byte[] BuildIco(IReadOnlyList<int> sizes, IconVariant variant = IconVariant.Tile)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        if (sizes.Count == 0 || sizes.Any(s => s is < 8 or > 256)) throw new ArgumentOutOfRangeException(nameof(sizes), "Tamanhos de 8 a 256.");

        return IcoFile.Build(sizes.Select(size => Render(size, variant)).ToList());
    }

    // ------------------------------------------------------------------
    // Uso no aplicativo
    // ------------------------------------------------------------------

    // Só os BYTES do .ico ficam em cache estático (imutáveis, livres de thread). O BitmapFrame não pode: ele guarda o
    // decoder, que pertence à thread que o criou, mesmo congelado. Cada thread de UI cria o seu a partir dos bytes.
    private static readonly Lazy<byte[]> WindowIconBytes = new(() => BuildIco(IcoSizes), isThreadSafe: true);

    [ThreadStatic]
    private static BitmapFrame? _windowIcon;

    /// <summary>
    /// Ícone das janelas (todos os tamanhos; o Windows escolhe o melhor para cada DPI). Congelado.
    /// Um por thread de UI, criado sob demanda.
    /// </summary>
    internal static ImageSource WindowIcon => _windowIcon ??= CreateWindowIcon();

    private static BitmapFrame CreateWindowIcon()
    {
        using var stream = new MemoryStream(WindowIconBytes.Value);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        // O quadro guarda o decoder do .ico; o Window escolhe dali o tamanho certo para cada DPI.
        BitmapFrame frame = decoder.Frames.OrderBy(f => f.PixelWidth).Last();
        frame.Freeze();
        return frame;
    }
}