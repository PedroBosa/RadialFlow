using System;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using RadialFlow.Config;
using RadialFlow.UI;
using Xunit;

namespace RadialFlow.Tests;

/// <summary>Cores, geometria e pixels da roda: tudo que dá para provar sem abrir a janela.</summary>
public sealed class RadialVisualsTests
{
    private static readonly Color[] Backgrounds =
        [Colors.Black, Colors.White, Color.FromRgb(0x80, 0x80, 0x80), Color.FromRgb(0xE8, 0x11, 0x23), Color.FromRgb(0x00, 0x78, 0xD4), Color.FromRgb(0xFF, 0xF1, 0x00)];

    public static TheoryData<bool, string, byte, byte, byte> AccentMatrix()
    {
        var data = new TheoryData<bool, string, byte, byte, byte>();
        // As opções oferecidas + extremos que um "Cor do Windows" pode trazer.
        (string Name, Color Color)[] accents =
        [
            ("azul", Color.FromRgb(0x3B, 0x82, 0xF6)), ("violeta", Color.FromRgb(0x8B, 0x5C, 0xF6)),
            ("verde-agua", Color.FromRgb(0x14, 0xB8, 0xA6)), ("verde", Color.FromRgb(0x22, 0xC5, 0x5E)),
            ("laranja", Color.FromRgb(0xF9, 0x73, 0x16)), ("rosa", Color.FromRgb(0xEC, 0x48, 0x99)),
            ("amarelo", Color.FromRgb(0xFF, 0xF1, 0x00)), ("ciano", Color.FromRgb(0x00, 0xE5, 0xFF)),
            ("vermelho", Color.FromRgb(0xE8, 0x11, 0x23)), ("branco", Colors.White),
            ("preto", Colors.Black), ("cinza", Color.FromRgb(0x7A, 0x7A, 0x7A)),
        ];

        foreach (bool light in new[] { false, true })
            foreach (var (name, c) in accents)
                data.Add(light, name, c.R, c.G, c.B);
        return data;
    }

    // ------------------------------------------------------------------
    // Contraste (acessibilidade)
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AccentMatrix))]
    public void Tema_TextoSobreODestaque_TemContrasteAA_EmTodoOGradiente(bool light, string name, byte r, byte g, byte b)
    {
        RadialTheme theme = RadialTheme.Create(light, Color.FromRgb(r, g, b));

        Assert.True(ColorMath.Contrast(theme.HoverInk, theme.HoverTop) >= RadialTheme.MinTextContrast, $"{name}/topo");
        Assert.True(ColorMath.Contrast(theme.HoverInk, theme.HoverBottom) >= RadialTheme.MinTextContrast, $"{name}/base");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tema_TextoEmRepouso_LeSobreQualquerFundoDesfocado(bool light)
    {
        RadialTheme theme = RadialTheme.Create(light, Color.FromRgb(0x3B, 0x82, 0xF6));

        foreach (Color background in Backgrounds)
        {
            Color surface = ColorMath.Over(theme.SegmentRest, ColorMath.Over(theme.Tint, background));
            Assert.True(ColorMath.Contrast(theme.Ink, surface) >= RadialTheme.MinTextContrast,
                $"{(light ? "claro" : "escuro")} sobre {background}: {ColorMath.Contrast(theme.Ink, surface):0.0}");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tema_MiolhoEEtiqueta_TemContraste(bool light)
    {
        RadialTheme theme = RadialTheme.Create(light, Color.FromRgb(0x3B, 0x82, 0xF6));

        foreach (Color background in Backgrounds)
        {
            Assert.True(ColorMath.Contrast(theme.HubInk, ColorMath.Over(theme.HubFill, background)) >= RadialTheme.MinTextContrast);
            Assert.True(ColorMath.Contrast(theme.PillInk, ColorMath.Over(theme.PillFill, background)) >= RadialTheme.MinTextContrast);
        }
    }

    [Theory]
    [MemberData(nameof(AccentMatrix))]
    public void Tema_AltoContraste_TextoSobreODestaque_TemContrasteAA_EmTodoOGradiente(
        bool light, string name, byte r, byte g, byte b)
    {
        RadialTheme theme = RadialTheme.Create(light, Color.FromRgb(r, g, b), highContrast: true);

        Assert.True(ColorMath.Contrast(theme.HoverInk, theme.HoverTop) >= RadialTheme.MinTextContrast, $"{name}/topo");
        Assert.True(ColorMath.Contrast(theme.HoverInk, theme.HoverBottom) >= RadialTheme.MinTextContrast, $"{name}/base");
    }

    [Theory]
    [MemberData(nameof(AccentMatrix))]
    public void Tema_AltoContraste_SuperficieOpaca_ETextoETracoLegiveis(
        bool light, string name, byte r, byte g, byte b)
    {
        RadialTheme theme = RadialTheme.Create(light, Color.FromRgb(r, g, b), highContrast: true);

        // O preset não depende da captura do fundo: as superfícies são opacas e o texto fica no extremo oposto.
        Assert.Equal(255, theme.Tint.A);
        Assert.Equal(255, theme.HubFill.A);
        Assert.Equal(255, theme.PillFill.A);
        Assert.Equal(0, theme.SegmentRest.A);
        Assert.True(ColorMath.Contrast(theme.Ink, theme.Tint) >= 7.0, $"{name}: texto/superfície");
        Assert.True(ColorMath.Contrast(theme.HubInk, theme.HubFill) >= 7.0, $"{name}: miolo");
        Assert.True(ColorMath.Contrast(theme.PillInk, theme.PillFill) >= 7.0, $"{name}: dica");
        Assert.True(ColorMath.Contrast(theme.DwellArc, theme.Tint) >= RadialTheme.MinTextContrast, $"{name}: traço de espera");
    }

    [Fact]
    public void Tema_AltoContraste_MantemODestaqueEscolhido()
    {
        RadialTheme theme = RadialTheme.Create(light: false, Color.FromRgb(0x3B, 0x82, 0xF6), highContrast: true);

        Assert.True(theme.HoverTop.B > theme.HoverTop.R, "o destaque ativo continua azul");
        Assert.True(theme.Glow.B > theme.Glow.R);
        Assert.NotEqual(theme.Tint, theme.HoverTop);
    }

    [Fact]
    public void Tema_Padrao_ContinuaTranslucido()
    {
        Assert.True(RadialTheme.Create(light: false, Color.FromRgb(0x3B, 0x82, 0xF6)).Tint.A < 255);
    }

    [Fact]
    public void Tema_AzulPadrao_PrefereTextoBrancoEscurecendoODestaque()
    {
        RadialTheme theme = RadialTheme.Create(light: false, Color.FromRgb(0x3B, 0x82, 0xF6));

        Assert.Equal(Colors.White, theme.HoverInk);
        Assert.True(ColorMath.Luminance(theme.HoverBottom) < ColorMath.Luminance(theme.HoverTop));
    }

    [Fact]
    public void Tema_MatizClaroDemais_UsaTextoEscuroEClareiaODestaque()
    {
        // Amarelo não sustenta texto branco sem virar marrom: usa tinta escura sobre amarelo vivo.
        RadialTheme theme = RadialTheme.Create(light: false, Color.FromRgb(0xFF, 0xF1, 0x00));

        Assert.Equal(ColorMath.DarkInk, theme.HoverInk);
    }

    [Fact]
    public void Tema_AcentoNeutro_ContinuaNeutro()
    {
        foreach (Color neutral in new[] { Colors.Black, Colors.White, Color.FromRgb(0x7A, 0x7A, 0x7A) })
        {
            RadialTheme theme = RadialTheme.Create(false, neutral);
            var (_, saturation, _) = ColorMath.ToHsl(theme.HoverTop);
            Assert.True(saturation < 0.1, $"{neutral} virou colorido (S={saturation:0.00}).");
        }
    }

    // ------------------------------------------------------------------
    // Escolha da cor
    // ------------------------------------------------------------------

    [Fact]
    public void AccentChoices_ChavesUnicasEPadraoEhAzul()
    {
        string[] keys = RadialTheme.AccentChoices.Select(c => c.Key).ToArray();

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("Blue", keys[0]);
        Assert.Contains("Windows", keys);
        Assert.Equal("Blue", new AppSettings().Accent);
    }

    [Theory]
    [InlineData("Blue")]
    [InlineData("blue")]
    [InlineData("  BLUE ")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Magenta-que-nao-existe")]
    public void ResolveAccent_NomeDesconhecidoOuVazio_CaiNoAzulPadrao(string? key)
    {
        Assert.Equal(RadialTheme.ResolveAccent("Blue"), RadialTheme.ResolveAccent(key));
    }

    [Fact]
    public void ResolveAccent_Windows_UsaACorDoSistema()
    {
        Assert.Equal(RadialTheme.ReadAccent(), RadialTheme.ResolveAccent("Windows"));
    }

    [Fact]
    public void Configuracoes_CorInvalidaNoArquivo_NaoInvalidaOutrasOpcoes()
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>("""{ "Accent": 42, "DeadzonePixels": 22 }""".Replace("42", "\"Roxo?\""),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Equal(22, loaded!.DeadzonePixels);
        Assert.Equal(RadialTheme.ResolveAccent("Blue"), RadialTheme.ResolveAccent(loaded.Accent));
    }

    // ------------------------------------------------------------------
    // Matemática de cor
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0x3B, 0x82, 0xF6)]
    [InlineData(0xE8, 0x11, 0x23)]
    [InlineData(0x22, 0xC5, 0x5E)]
    [InlineData(0x10, 0x20, 0x30)]
    [InlineData(0xFF, 0xFF, 0xFF)]
    [InlineData(0x00, 0x00, 0x00)]
    public void HslIdaEVolta_PreservaACor(int r, int g, int b)
    {
        var (h, s, l) = ColorMath.ToHsl(Color.FromRgb((byte)r, (byte)g, (byte)b));

        Color back = ColorMath.FromHsl(h, s, l);

        Assert.InRange(back.R, r - 1, r + 1);
        Assert.InRange(back.G, g - 1, g + 1);
        Assert.InRange(back.B, b - 1, b + 1);
    }

    [Fact]
    public void Contraste_ValoresConhecidosDoWcag()
    {
        Assert.Equal(21.0, ColorMath.Contrast(Colors.Black, Colors.White), precision: 6);
        Assert.Equal(1.0, ColorMath.Contrast(Colors.White, Colors.White), precision: 6);
    }

    [Fact]
    public void Over_CompoeCorComAlfaSobreFundoOpaco()
    {
        Assert.Equal(Color.FromRgb(127, 127, 127), ColorMath.Over(Color.FromArgb(128, 0, 0, 0), Colors.White));   // 128/255 = 50,2% de preto
        Assert.Equal(Colors.Red, ColorMath.Over(Colors.Red, Colors.Blue));
    }

    // ------------------------------------------------------------------
    // Geometria das fatias
    // ------------------------------------------------------------------

    private const double Cx = 220, Cy = 220, Inner = 58, Outer = 170;

    private static Geometry Slice(int index, int count, double gap = 3.2, double corner = 9) =>
        RadialGeometry.BuildRoundedSlice(index, count, Cx, Cy, Inner, Outer, gap, corner);

    private static Point At(double angleDeg, double radius) => RadialGeometry.Polar(Cx, Cy, radius, angleDeg);

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void FatiaArredondada_ContemOCentroDaPropriaFatia_ENenhumaOutra(int count)
    {
        for (int i = 0; i < count; i++)
        {
            Point center = At(RadialGeometry.CenterAngle(i, count), (Inner + Outer) / 2);

            for (int j = 0; j < count; j++)
                Assert.Equal(i == j, Slice(j, count).FillContains(center));
        }
    }

    [Fact]
    public void FatiaArredondada_NaoCobreOBuracoCentralNemOExterior()
    {
        Geometry slice = Slice(0, 6);

        Assert.False(slice.FillContains(new Point(Cx, Cy)));
        Assert.False(slice.FillContains(At(-90, Inner - 4)));
        Assert.False(slice.FillContains(At(-90, Outer + 4)));
    }

    [Fact]
    public void FatiaArredondada_FicaDentroDoCirculoExterno()
    {
        for (int count = 2; count <= RadialGeometry.MaxSlices; count++)
        {
            for (int i = 0; i < count; i++)
            {
                Rect bounds = Slice(i, count).Bounds;
                Assert.True(bounds.Left >= Cx - Outer - 0.5 && bounds.Right <= Cx + Outer + 0.5, $"{i}/{count} horizontal");
                Assert.True(bounds.Top >= Cy - Outer - 0.5 && bounds.Bottom <= Cy + Outer + 0.5, $"{i}/{count} vertical");
            }
        }
    }

    [Fact]
    public void FatiaArredondada_AFrestaEntreVizinhasTemLarguraConstante()
    {
        // Fatias 0 e 1 de 6 dividem o limite em -60°. Sobre a reta do limite (perpendicular à fresta),
        // nenhuma das duas cobre o ponto, perto do círculo interno e perto do externo.
        double boundary = -60;
        foreach (double radius in new[] { Inner + 25, (Inner + Outer) / 2, Outer - 25 })
        {
            Point onBoundary = At(boundary, radius);
            Assert.False(Slice(0, 6).FillContains(onBoundary), $"fatia 0 em r={radius}");
            Assert.False(Slice(1, 6).FillContains(onBoundary), $"fatia 1 em r={radius}");

            // 3 px para cada lado do limite já está dentro de uma das duas (fresta de 3,2 px).
            Vector along = (At(boundary, radius) - new Point(Cx, Cy));
            along.Normalize();
            var normal = new Vector(-along.Y, along.X);
            Assert.True(Slice(0, 6).FillContains(onBoundary + normal * -3.0) || Slice(0, 6).FillContains(onBoundary + normal * 3.0));
        }
    }

    [Fact]
    public void FatiaArredondada_OsCantosSaoArredondados_NaoPontudos()
    {
        // O canto "agudo" externo da fatia 0 fica fora da forma arredondada, mas ~12 px para dentro está dentro.
        double outerShift = Math.Asin((3.2 / 2) / Outer) * 180 / Math.PI;
        double startBoundary = -90 - 30;
        Point sharpCorner = At(startBoundary + outerShift, Outer);
        Vector towardCenter = new Point(Cx, Cy) - sharpCorner;
        towardCenter.Normalize();

        Assert.False(Slice(0, 6).FillContains(sharpCorner + towardCenter * 1.0));
        Assert.True(Slice(0, 6).FillContains(sharpCorner + towardCenter * 14.0 + new Vector(6, 0)));

        // Com raio zero o mesmo ponto passa a estar dentro (a diferença é o arredondamento).
        Assert.True(Slice(0, 6, corner: 0).FillContains(sharpCorner + towardCenter * 1.0 + new Vector(3, 0)));
    }

    [Fact]
    public void FatiaArredondada_RaioExageradoECortadoParaCaber()
    {
        Geometry slice = Slice(0, 8, corner: 500);

        Assert.True(slice.FillContains(At(RadialGeometry.CenterAngle(0, 8), (Inner + Outer) / 2)));
        Assert.True(slice.Bounds.Width > 0 && slice.Bounds.Height > 0);
    }

    [Fact]
    public void FatiaArredondada_ECongeladaEPodeSerUsadaEmQualquerThread()
    {
        Assert.True(Slice(0, 6).IsFrozen);
    }

    // ------------------------------------------------------------------
    // Pixels (vidro fosco)
    // ------------------------------------------------------------------

    private static byte[] Solid(int w, int h, byte b, byte g, byte r)
    {
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 0; }
        return pixels;
    }

    [Fact]
    public void Downscale_FazMediaDosBlocos_EForcaAlfaOpaco()
    {
        // 4x4: metade esquerda preta, metade direita branca; fator 4 => um pixel cinza médio.
        var src = new byte[4 * 4 * 4];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                byte v = (byte)(x < 2 ? 0 : 255);
                int i = (y * 4 + x) * 4;
                src[i] = src[i + 1] = src[i + 2] = v;
            }

        (byte[] pixels, int w, int h) = ImageOps.Downscale(src, 4, 4, 4);

        Assert.Equal((1, 1), (w, h));
        Assert.Equal(127, pixels[0]);
        Assert.Equal(255, pixels[3]);   // capturas de tela vêm com alfa 0
    }

    [Fact]
    public void Downscale_TamanhoNaoMultiplo_NaoPerdeAsPontas()
    {
        (byte[] pixels, int w, int h) = ImageOps.Downscale(Solid(10, 7, 10, 20, 30), 10, 7, 4);

        Assert.Equal((2, 1), (w, h));
        Assert.All(Enumerable.Range(0, w * h), i => Assert.Equal(new byte[] { 10, 20, 30, 255 }, pixels.Skip(i * 4).Take(4)));
    }

    [Fact]
    public void BoxBlur_ImagemUniforme_NaoMudaNemEscureceAsBordas()
    {
        byte[] pixels = Solid(20, 20, 200, 100, 50);
        byte[] expected = (byte[])pixels.Clone();

        ImageOps.BoxBlur(pixels, 20, 20, radius: 5);

        Assert.Equal(expected, pixels);
    }

    [Fact]
    public void BoxBlur_SuavizaABordaDeUmBlocoClaro_EConservaOBrilhoTotal()
    {
        // Um bloco claro de 9x9 no meio (como uma miniatura): as bordas ficam suaves e o brilho se conserva.
        // (Um único pixel não serve de teste: em 8 bits ele se espalha abaixo de 0,5 e arredonda para zero.)
        const int size = 41;
        byte[] pixels = Solid(size, size, 0, 0, 0);
        for (int y = 16; y < 25; y++)
            for (int x = 16; x < 25; x++)
            {
                int i = (y * size + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
            }
        long before = pixels.Where((_, i) => i % 4 != 3).Sum(v => (long)v);
        int insideEdge = (20 * size + 16) * 4;       // 1ª coluna do bloco: era 255
        int outsideEdge = (20 * size + 14) * 4;      // 2 px fora do bloco: era 0

        ImageOps.BoxBlur(pixels, size, size, radius: 3);

        Assert.InRange(pixels[insideEdge], 1, 254);  // a borda ficou intermediária
        Assert.True(pixels[outsideEdge] > 0, "a luz não vazou para fora do bloco");
        long after = pixels.Where((_, i) => i % 4 != 3).Sum(v => (long)v);
        Assert.InRange(after, before * 0.97, before * 1.03);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 0)]
    public void BoxBlur_ParametrosInuteis_NaoFazemNada(int radius, int passes, int _)
    {
        byte[] pixels = Solid(8, 8, 1, 2, 3);
        byte[] expected = (byte[])pixels.Clone();

        ImageOps.BoxBlur(pixels, 8, 8, radius, passes == 0 ? 1 : passes);
        ImageOps.BoxBlur(pixels, 8, 8, radius: 0);

        Assert.Equal(expected, pixels);
    }

    [Fact]
    public void Saturate_CinzaContinuaCinza_ECorFicaMaisViva()
    {
        byte[] gray = Solid(2, 2, 90, 90, 90);
        ImageOps.Saturate(gray, 1.5);
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(90, gray[i * 4]));

        byte[] color = Solid(2, 2, 100, 120, 200);   // BGR: azulado
        ImageOps.Saturate(color, 1.5);
        Assert.True(color[2] > 200 - 1 && color[0] < 100, "saturar deve afastar os canais do cinza");
        Assert.True(color[0] >= 0 && color[2] <= 255);
    }

    [Fact]
    public void FrostedBackdrop_SemCaptura_DevolveNull()
    {
        Assert.Null(FrostedBackdrop.FromSnapshot(null));
        Assert.Null(ScreenSnapshot.Capture(0, 0, 0));
        Assert.Null(ScreenSnapshot.Capture(0, 0, 100_000));
    }
}
