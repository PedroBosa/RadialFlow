using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RadialFlow.UI;
using Xunit;

namespace RadialFlow.Tests;

/// <summary>
/// Ícone do aplicativo: estrutura do .ico, pixels de cada variante, ida e volta por HICON e o guarda
/// que avisa quando Assets/RadialFlow.ico fica diferente do que o código desenha.
/// </summary>
public sealed class AppIconTests
{
    // ------------------------------------------------------------------
    // Utilitários
    // ------------------------------------------------------------------

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("Falha na thread de UI do teste.", error);
    }

    private readonly record struct Px(byte B, byte G, byte R, byte A);

    private static Px At(byte[] pixels, int size, int x, int y)
    {
        int i = (y * size + x) * 4;
        return new Px(pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
    }

    private static Px AtPolar(byte[] pixels, int size, double angleDeg, double radiusFraction)
    {
        double rad = angleDeg * Math.PI / 180;
        double c = size / 2.0;
        return At(pixels, size, (int)Math.Round(c + Math.Cos(rad) * radiusFraction * size), (int)Math.Round(c + Math.Sin(rad) * radiusFraction * size));
    }

    private static bool IsBlue(Px p) => p.A > 200 && p.B > p.R + 70;

    private static string RepoFile(params string[] parts) =>
        Path.GetFullPath(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", .. parts]));

    /// <summary>Diferença média absoluta por canal entre duas imagens do mesmo tamanho (0 = idênticas).</summary>
    private static double MeanDifference(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        long sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }

    // ------------------------------------------------------------------
    // Detalhe por tamanho
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(16, 4)]
    [InlineData(20, 4)]
    [InlineData(24, 4)]
    [InlineData(27, 4)]
    [InlineData(28, 6)]
    [InlineData(32, 6)]
    [InlineData(256, 6)]
    public void SliceCount_PoucasFatiasEmTamanhoPequeno(int size, int expected)
    {
        Assert.Equal(expected, AppIconRenderer.SliceCount(size));
    }

    [Fact]
    public void FatiaAzulExisteEmTodoTamanhoDoIco()
    {
        Assert.InRange(AppIconRenderer.HighlightIndex, 0, AppIconRenderer.SliceCount(16) - 1);
        Assert.InRange(AppIconRenderer.HighlightIndex, 0, AppIconRenderer.SliceCount(256) - 1);
    }

    // ------------------------------------------------------------------
    // Estrutura do .ico
    // ------------------------------------------------------------------

    [Fact]
    public void Ico_TemCabecalhoEUmaEntradaPorTamanho_ComOffsetsConsistentes()
    {
        RunSta(() =>
        {
            byte[] ico = AppIconRenderer.BuildIco(AppIconRenderer.IcoSizes);
            using var reader = new BinaryReader(new MemoryStream(ico));

            Assert.Equal(0, reader.ReadUInt16());                      // reservado
            Assert.Equal(1, reader.ReadUInt16());                      // tipo ícone
            int count = reader.ReadUInt16();
            Assert.Equal(AppIconRenderer.IcoSizes.Length, count);

            long expectedOffset = 6 + 16 * count;
            for (int i = 0; i < count; i++)
            {
                int size = AppIconRenderer.IcoSizes[i];
                byte w = reader.ReadByte(), h = reader.ReadByte();
                reader.ReadBytes(2);                                   // paleta, reservado
                Assert.Equal(1, reader.ReadUInt16());                  // planos
                Assert.Equal(32, reader.ReadUInt16());                 // bits por pixel
                uint bytes = reader.ReadUInt32();
                uint offset = reader.ReadUInt32();

                Assert.Equal(size >= 256 ? 0 : size, w);               // 0 significa 256
                Assert.Equal(w, h);
                Assert.Equal((uint)expectedOffset, offset);            // quadros contíguos, sem lacunas
                expectedOffset += bytes;
            }

            Assert.Equal(ico.Length, expectedOffset);                  // sem bytes sobrando nem faltando
        });
    }

    [Fact]
    public void Ico_QuadrosPequenosSaoDibEOde256EPng()
    {
        RunSta(() =>
        {
            byte[] ico = AppIconRenderer.BuildIco([16, 256]);
            uint Offset(int entry) => BitConverter.ToUInt32(ico, 6 + entry * 16 + 12);

            // 16 px: BITMAPINFOHEADER (40), altura dobrada (cor + máscara), 32 bpp.
            int dib = (int)Offset(0);
            Assert.Equal(40, BitConverter.ToInt32(ico, dib));
            Assert.Equal(16, BitConverter.ToInt32(ico, dib + 4));
            Assert.Equal(32, BitConverter.ToInt32(ico, dib + 8));
            Assert.Equal(32, BitConverter.ToInt16(ico, dib + 14));

            // 256 px: assinatura PNG.
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, ico.AsSpan((int)Offset(1), 4).ToArray());
        });
    }

    [Fact]
    public void Ico_ODecoderDoWpfLeTodosOsQuadros()
    {
        RunSta(() =>
        {
            byte[] ico = AppIconRenderer.BuildIco(AppIconRenderer.IcoSizes);

            BitmapDecoder decoder = BitmapDecoder.Create(new MemoryStream(ico), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

            Assert.Equal(AppIconRenderer.IcoSizes.OrderBy(s => s), decoder.Frames.Select(f => f.PixelWidth).OrderBy(s => s));
            Assert.All(decoder.Frames, f => Assert.Equal(f.PixelWidth, f.PixelHeight));
        });
    }

    [Fact]
    public void Ico_OQuadroDecodificadoConfereComORenderizado()
    {
        RunSta(() =>
        {
            // Prova que o DIB (de baixo para cima, alfa não pré-multiplicado, máscara) foi escrito corretamente.
            byte[] ico = AppIconRenderer.BuildIco([16, 32, 48, 256]);
            BitmapDecoder decoder = BitmapDecoder.Create(new MemoryStream(ico), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

            foreach (BitmapFrame frame in decoder.Frames)
            {
                int size = frame.PixelWidth;
                byte[] decoded = AppIconRenderer.GetPixels(new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0));
                byte[] expected = AppIconRenderer.GetPixels(AppIconRenderer.Render(size, IconVariant.Tile));

                Assert.True(MeanDifference(decoded, expected) < 1.0, $"{size}px: DIB diferente do renderizado");
            }
        });
    }

    [Theory]
    [InlineData(7)]
    [InlineData(300)]
    public void Ico_TamanhoForaDoIntervalo_Lanca(int size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AppIconRenderer.BuildIco([size]));
    }

    [Fact]
    public void Ico_ListaVazia_Lanca()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AppIconRenderer.BuildIco([]));
    }

    // ------------------------------------------------------------------
    // Pixels: o desenho é o que dizemos que é
    // ------------------------------------------------------------------

    [Fact]
    public void Azulejo_CantosTransparentes_FundoEscuroOpaco()
    {
        RunSta(() =>
        {
            const int size = 256;
            byte[] pixels = AppIconRenderer.GetPixels(AppIconRenderer.Render(size, IconVariant.Tile));

            Assert.Equal(0, At(pixels, size, 0, 0).A);                 // cantos arredondados
            Assert.Equal(0, At(pixels, size, size - 1, size - 1).A);

            Px background = At(pixels, size, (int)(size * 0.10), size / 2);   // dentro do azulejo, fora do anel
            Assert.Equal(255, background.A);
            Assert.True(background.R < 60 && background.G < 70 && background.B < 100, $"fundo deveria ser escuro: {background}");
        });
    }

    [Fact]
    public void Azulejo_TemAFatiaAzulNoAltoADireita_EAsDemaisClaras()
    {
        RunSta(() =>
        {
            const int size = 256;
            byte[] pixels = AppIconRenderer.GetPixels(AppIconRenderer.Render(size, IconVariant.Tile));

            // 6 fatias, a de índice 1 fica a -30° (alto à direita); raio no meio do anel.
            Px lit = AtPolar(pixels, size, -30, 0.355 * 0.71 * 1.05);
            Assert.True(IsBlue(lit), $"fatia ativa deveria ser azul: {lit}");

            foreach (double angle in new[] { -90, 30, 90, 150, -150 })
            {
                Px rest = AtPolar(pixels, size, angle, 0.355 * 0.71);
                Assert.False(IsBlue(rest), $"fatia a {angle}° não deveria ser azul: {rest}");
                Assert.True(rest.R > 200 && rest.G > 200, $"fatia a {angle}° deveria ser clara: {rest}");
            }
        });
    }

    [Fact]
    public void Azulejo_TemOMiolo_NoCentro()
    {
        RunSta(() =>
        {
            const int size = 256;
            byte[] pixels = AppIconRenderer.GetPixels(AppIconRenderer.Render(size, IconVariant.Tile));

            Px hub = At(pixels, size, size / 2, size / 2);
            Assert.True(hub.R > 200 && hub.A == 255, $"o miolo (hub) deveria ser claro: {hub}");

            // E há um anel escuro entre o miolo e as fatias (o buraco do anel).
            Px gap = AtPolar(pixels, size, 90, 0.355 * 0.42 * 0.80);
            Assert.True(gap.R < 80, $"entre o miolo e o anel deveria ser escuro: {gap}");
        });
    }

    [Fact]
    public void MiolhoSoAparecePerto20pxOuMais()
    {
        RunSta(() =>
        {
            byte[] tiny = AppIconRenderer.GetPixels(AppIconRenderer.Render(16, IconVariant.Glyph));
            byte[] small = AppIconRenderer.GetPixels(AppIconRenderer.Render(20, IconVariant.Glyph));
            byte[] large = AppIconRenderer.GetPixels(AppIconRenderer.Render(32, IconVariant.Glyph));

            Assert.Equal(0, At(tiny, 16, 8, 8).A);       // 16 px: centro vazio (não há pixels para o miolo)
            Assert.True(At(small, 20, 10, 10).A > 200);  // 20 px: miolo desenhado
            Assert.Equal(255, At(large, 32, 16, 16).A);  // 32 px: miolo desenhado
        });
    }

    [Fact]
    public void Glifo_FundoTransparente_ComAnelEFatiaAzul()
    {
        RunSta(() =>
        {
            const int size = 64;
            byte[] pixels = AppIconRenderer.GetPixels(AppIconRenderer.Render(size, IconVariant.Glyph));

            Assert.Equal(0, At(pixels, size, 0, 0).A);
            Assert.Equal(0, At(pixels, size, size - 1, 2).A);
            Assert.True(IsBlue(AtPolar(pixels, size, -30, 0.45 * 0.71)), "fatia ativa");
            Assert.False(IsBlue(AtPolar(pixels, size, 150, 0.45 * 0.71)), "fatia em repouso");
        });
    }

    [Fact]
    public void Glifo_Pausado_NaoTemNenhumPixelAzul_ENemTotalmenteOpaco()
    {
        RunSta(() =>
        {
            const int size = 64;
            byte[] active = AppIconRenderer.GetPixels(AppIconRenderer.Render(size, IconVariant.Glyph));
            byte[] paused = AppIconRenderer.GetPixels(AppIconRenderer.Render(size, IconVariant.Glyph, paused: true));

            bool Any(byte[] px) { for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) if (IsBlue(At(px, size, x, y))) return true; return false; }
            Assert.True(Any(active));
            Assert.False(Any(paused));

            // O anel pausado é mais "apagado" que o ativo (transparência parcial).
            Px ring = AtPolar(paused, size, 150, 0.45 * 0.71);
            Assert.InRange(ring.A, 100, 200);
        });
    }

    [Fact]
    public void Glifo_AcompanhaOTemaDaBarraDeTarefas()
    {
        RunSta(() =>
        {
            const int size = 64;
            Px onDark = AtPolar(AppIconRenderer.GetPixels(AppIconRenderer.Render(size, IconVariant.Glyph, lightBackground: false)), size, 150, 0.45 * 0.71);
            Px onLight = AtPolar(AppIconRenderer.GetPixels(AppIconRenderer.Render(size, IconVariant.Glyph, lightBackground: true)), size, 150, 0.45 * 0.71);

            Assert.True(onDark.R > 200, $"barra escura → anel claro: {onDark}");
            Assert.True(onLight.R < 80, $"barra clara → anel escuro: {onLight}");
        });
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void FresaEntreFatiasTemPeloMenosUmPixel_EmTamanhosPequenos(int size)
    {
        RunSta(() =>
        {
            byte[] pixels = AppIconRenderer.GetPixels(AppIconRenderer.Render(size, IconVariant.Glyph));
            int count = AppIconRenderer.SliceCount(size);

            // No limite entre as fatias 2 e 3 (ângulo da fronteira) o pixel do meio do anel fica transparente.
            double boundary = -90 + (2.5) * (360.0 / count);
            Px between = AtPolar(pixels, size, boundary, 0.45 * 0.71);
            Assert.True(between.A < 140, $"{size}px: deveria haver fresta visível entre as fatias: {between}");
        });
    }

    [Fact]
    public void Render_EDeterministico()
    {
        RunSta(() =>
        {
            byte[] a = AppIconRenderer.GetPixels(AppIconRenderer.Render(48, IconVariant.Tile));
            byte[] b = AppIconRenderer.GetPixels(AppIconRenderer.Render(48, IconVariant.Tile));

            Assert.Equal(a, b);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Render_TodoTamanhoTemAlgumPixelVisivel(bool tile)
    {
        IconVariant variant = tile ? IconVariant.Tile : IconVariant.Glyph;
        RunSta(() =>
        {
            foreach (int size in AppIconRenderer.IcoSizes)
            {
                byte[] pixels = AppIconRenderer.GetPixels(AppIconRenderer.Render(size, variant));
                int visible = 0;
                for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] > 128) visible++;

                Assert.True(visible > size * size / 6, $"{size}px {variant}: só {visible} pixels visíveis");
            }
        });
    }

    // ------------------------------------------------------------------
    // HICON da bandeja
    // ------------------------------------------------------------------

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void Hicon_IdaEVolta_PreservaOsPixels_SemEscurecerAsBordas(int size)
    {
        RunSta(() =>
        {
            BitmapSource source = AppIconRenderer.Render(size, IconVariant.Glyph);
            byte[] sent = AppIconRenderer.GetPixels(source);

            IntPtr hicon = TrayIconService.CreateHicon(sent, size);
            Assert.NotEqual(IntPtr.Zero, hicon);
            try
            {
                BitmapSource back = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                Assert.Equal(size, back.PixelWidth);

                byte[] received = AppIconRenderer.GetPixels(new FormatConvertedBitmap(back, PixelFormats.Bgra32, null, 0));

                // Se o alfa fosse tratado como pré-multiplicado, as bordas semitransparentes sairiam escuras/claras demais.
                Assert.True(MeanDifference(sent, received) < 3.0, $"{size}px: diferença média {MeanDifference(sent, received):0.00}");
            }
            finally
            {
                DestroyIcon(hicon);
            }
        });
    }

    [Fact]
    public void Hicon_PixelsInsuficientes_Lanca()
    {
        Assert.Throws<ArgumentException>(() => TrayIconService.CreateHicon(new byte[10], 16));
        Assert.Throws<ArgumentNullException>(() => TrayIconService.CreateHicon(null!, 16));
    }

    [Fact]
    public void TamanhoDoIconePequenoDoSistema_EValido()
    {
        Assert.InRange(TrayIconService.GetSmallIconSize(), 8, 256);
    }

    // ------------------------------------------------------------------
    // Ícone das janelas
    // ------------------------------------------------------------------

    [Fact]
    public void IconeDasJanelas_TemTodosOsTamanhos_EEstaCongelado()
    {
        RunSta(() =>
        {
            ImageSource icon = AppIconRenderer.WindowIcon;

            Assert.True(icon.IsFrozen);
            var frame = Assert.IsAssignableFrom<BitmapFrame>(icon);
            Assert.Equal(256, frame.PixelWidth);
            Assert.Equal(AppIconRenderer.IcoSizes.Length, frame.Decoder.Frames.Count);
        });
    }

    [Fact]
    public void IconeDasJanelas_FuncionaEmThreadsDeUiDiferentes()
    {
        // Regressão: o BitmapFrame guarda o decoder, que pertence à thread que o criou. Uma estática compartilhada
        // fazia a SEGUNDA thread falhar com "O thread de chamada não pode acessar este objeto".
        ImageSource? first = null;
        ImageSource? second = null;

        RunSta(() => first = AppIconRenderer.WindowIcon);
        RunSta(() =>
        {
            second = AppIconRenderer.WindowIcon;
            Assert.Equal(256, ((BitmapFrame)second).PixelWidth);   // usável nesta thread
        });

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);   // cada thread tem o seu
    }

    [Fact]
    public void IconeDasJanelas_NaMesmaThread_EReaproveitado()
    {
        RunSta(() => Assert.Same(AppIconRenderer.WindowIcon, AppIconRenderer.WindowIcon));
    }

    // ------------------------------------------------------------------
    // Ícone embutido no executável
    // ------------------------------------------------------------------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [Fact]
    public void Executavel_TemOIconeDoAplicativoEmbutido()
    {
        RunSta(() =>
        {
            // O apphost (RadialFlow.exe) é copiado para a saída dos testes junto da DLL; senão procura no projeto do app.
            string[] candidates =
            [
                Path.Combine(AppContext.BaseDirectory, "RadialFlow.exe"),
                RepoFile("bin", "Debug", "net10.0-windows", "RadialFlow.exe"),
                RepoFile("bin", "Release", "net10.0-windows", "RadialFlow.exe")
            ];
            string exe = candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("RadialFlow.exe não foi encontrado: compile o projeto do app.");

            var small = new IntPtr[1];
            Assert.Equal(1u, ExtractIconEx(exe, 0, null, small, 1));   // o executável TEM ícone
            try
            {
                BitmapSource extracted = Imaging.CreateBitmapSourceFromHIcon(small[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                int size = extracted.PixelWidth;
                Assert.Contains(size, AppIconRenderer.IcoSizes);       // um dos tamanhos do .ico, não o ícone genérico

                byte[] fromExe = AppIconRenderer.GetPixels(new FormatConvertedBitmap(extracted, PixelFormats.Bgra32, null, 0));
                byte[] ours = AppIconRenderer.GetPixels(AppIconRenderer.Render(size, IconVariant.Tile));
                Assert.True(MeanDifference(fromExe, ours) < 3.0, $"{size}px: o ícone do .exe não é o do aplicativo (diferença {MeanDifference(fromExe, ours):0.0})");
            }
            finally
            {
                DestroyIcon(small[0]);
            }
        });
    }

    // ------------------------------------------------------------------
    // Arquivo versionado
    // ------------------------------------------------------------------

    [Fact]
    public void ArquivoIcoVersionado_ConfereComOQueOCodigoDesenha()
    {
        RunSta(() =>
        {
            string path = RepoFile("Assets", "RadialFlow.ico");
            Assert.True(File.Exists(path), $"Assets/RadialFlow.ico não encontrado em {path}");

            BitmapDecoder file = BitmapDecoder.Create(new MemoryStream(File.ReadAllBytes(path)), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

            Assert.Equal(AppIconRenderer.IcoSizes.OrderBy(s => s), file.Frames.Select(f => f.PixelWidth).OrderBy(s => s));

            foreach (BitmapFrame frame in file.Frames)
            {
                int size = frame.PixelWidth;
                byte[] onDisk = AppIconRenderer.GetPixels(new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0));
                byte[] fresh = AppIconRenderer.GetPixels(AppIconRenderer.Render(size, IconVariant.Tile));

                // Tolerância: o rasterizador do WPF pode variar 1–2 níveis entre máquinas; um desenho diferente passa MUITO disso.
                Assert.True(MeanDifference(onDisk, fresh) < 2.0,
                    $"{size}px: Assets/RadialFlow.ico está desatualizado. Rode: dotnet run --project tools/IconGenerator");
            }
        });
    }
}
