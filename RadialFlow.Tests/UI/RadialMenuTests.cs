using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RadialFlow.Config;
using RadialFlow.UI;
using Xunit;

namespace RadialFlow.Tests;

/// <summary>
/// Tempo para abrir submenus (antes fixo em 260 ms) e dimensionamento dos rótulos da roda.
/// A janela é criada FORA da tela (coordenadas -20000) e observada pela dica que ela mesma exibe.
/// </summary>
public sealed class RadialMenuTests
{
    private const int OriginX = -20000, OriginY = -20000;
    private const string HoverHint = "segure para ver as opções";

    // ------------------------------------------------------------------
    // Utilitários: thread STA + bomba de mensagens
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

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// true se a roda mostra um submenu. No submenu o miolo troca a marca de repouso pela seta "voltar";
    /// o texto da dica não serve de sinal (ao esconder a dica o texto antigo permanece, invisível).
    /// </summary>
    private static bool IsSubmenu(RadialMenuWindow wheel) => wheel.IdleDot.Visibility != System.Windows.Visibility.Visible;
    private static RadialSlice Slice(string label, IReadOnlyList<RadialSlice>? sub = null) =>
        new(label, "\uE8AB", () => { }, sub, $"Descrição de {label}");

    /// <summary>Roda com "Converter" (fatia 1, a -30°) contendo um submenu de 3 formatos.</summary>
    private static RadialMenuWindow CreateWheel(int dwellMilliseconds)
    {
        var formats = new List<RadialSlice> { Slice("PNG"), Slice("JPG"), Slice("WEBP") };
        var wheel = new RadialMenuWindow { DwellMilliseconds = dwellMilliseconds };
        wheel.Prewarm();
        wheel.Present(OriginX, OriginY, [Slice("Comprimir"), Slice("Converter", formats), Slice("Renomear")]);
        Pump(300);   // animação de entrada
        return wheel;
    }

    /// <summary>Move o cursor para a fatia 1 (de 3 fatias: centro a -90° + 120° = 30°, abaixo à direita).</summary>
    private static void HoverConverter(RadialMenuWindow wheel)
    {
        double angle = (-90 + 120) * Math.PI / 180;
        wheel.UpdateCursor(OriginX + (int)(150 * Math.Cos(angle)), OriginY + (int)(150 * Math.Sin(angle)));
    }

    // ------------------------------------------------------------------
    // Limites e padrão
    // ------------------------------------------------------------------

    [Fact]
    public void DwellMilliseconds_PadraoEhMaiorQueOsAntigos260ms()
    {
        Assert.Equal(650, RadialMenuWindow.DefaultDwellMilliseconds);
        Assert.True(RadialMenuWindow.DefaultDwellMilliseconds > 260);
        Assert.Equal(RadialMenuWindow.DefaultDwellMilliseconds, AppSettings.CreateDefault().DwellMilliseconds);
        Assert.Equal(RadialMenuWindow.DefaultDwellMilliseconds, new AppSettings().DwellMilliseconds);
    }

    [Theory]
    [InlineData(0, 200)]
    [InlineData(-500, 200)]
    [InlineData(199, 200)]
    [InlineData(200, 200)]
    [InlineData(900, 900)]
    [InlineData(2000, 2000)]
    [InlineData(2001, 2000)]
    [InlineData(int.MaxValue, 2000)]
    public void DwellMilliseconds_FicaDentroDosLimites(int requested, int expected)
    {
        RunSta(() =>
        {
            var wheel = new RadialMenuWindow { DwellMilliseconds = requested };
            Assert.Equal(expected, wheel.DwellMilliseconds);
            wheel.Close();
        });
    }

    [Fact]
    public void Configuracoes_ArquivoAntigoSemOCampo_UsaOPadraoNovo()
    {
        // settings.json gravado antes desta versão não tem DwellMilliseconds.
        var loaded = JsonSerializer.Deserialize<AppSettings>("""{ "DeadzonePixels": 20 }""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(loaded);
        Assert.Equal(RadialMenuWindow.DefaultDwellMilliseconds, loaded!.DwellMilliseconds);
        Assert.Equal(20, loaded.DeadzonePixels);
    }

    [Fact]
    public void Configuracoes_ValorGravadoSobreviveAIdaEVolta()
    {
        var original = new AppSettings { DwellMilliseconds = 1200 };

        string json = JsonSerializer.Serialize(original);
        var back = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.Equal(1200, back!.DwellMilliseconds);
    }

    // ------------------------------------------------------------------
    // O tempo realmente vale: o submenu só abre depois dele
    // ------------------------------------------------------------------

    [Fact]
    public void Submenu_ComTempoLongo_NaoAbreAntes_EAbreDepois()
    {
        RunSta(() =>
        {
            RadialMenuWindow wheel = CreateWheel(dwellMilliseconds: 1200);

            HoverConverter(wheel);
            Pump(700);   // bem mais que os 260 ms antigos, ainda abaixo de 1200

            Assert.Contains(HoverHint, wheel.HintText.Text);      // ainda é a roda principal
            Assert.False(IsSubmenu(wheel));

            Pump(900);   // total ~1600 ms > 1200

            Assert.True(IsSubmenu(wheel));        // abriu: agora é o submenu
            wheel.Close();
        });
    }

    [Fact]
    public void Submenu_ComTempoCurto_AbreRapido()
    {
        RunSta(() =>
        {
            RadialMenuWindow wheel = CreateWheel(dwellMilliseconds: 200);

            HoverConverter(wheel);
            Pump(700);

            Assert.True(IsSubmenu(wheel));
            wheel.Close();
        });
    }

    [Fact]
    public void Submenu_SairDaFatiaAntesDoTempo_CancelaAAbertura()
    {
        RunSta(() =>
        {
            RadialMenuWindow wheel = CreateWheel(dwellMilliseconds: 800);

            HoverConverter(wheel);
            Pump(300);
            wheel.UpdateCursor(OriginX, OriginY);   // volta ao centro antes de completar
            Pump(1200);

            Assert.False(IsSubmenu(wheel));   // nunca abriu
            wheel.Close();
        });
    }

    [Fact]
    public void Submenu_VoltarAoCentroPeloMesmoTempo_RetornaAoMenuAnterior()
    {
        RunSta(() =>
        {
            RadialMenuWindow wheel = CreateWheel(dwellMilliseconds: 400);

            HoverConverter(wheel);
            Pump(900);
            Assert.True(IsSubmenu(wheel));        // submenu aberto

            wheel.UpdateCursor(OriginX, OriginY);                  // centro: começa a contagem para voltar
            Pump(150);
            Assert.True(IsSubmenu(wheel));        // ainda no submenu

            Pump(700);
            Assert.False(IsSubmenu(wheel));  // voltou
            wheel.Close();
        });
    }

    [Fact]
    public void Voltar_CirculoDeEsperaApareceSoNoCentroDoSubmenu_ESomeAoSairOuAoVoltar()
    {
        RunSta(() =>
        {
            RadialMenuWindow wheel = CreateWheel(dwellMilliseconds: 400);

            wheel.UpdateCursor(OriginX, OriginY);
            Pump(100);
            Assert.False(wheel.IsBackDwellVisible);   // menu principal: centro não volta para lugar nenhum

            HoverConverter(wheel);
            Pump(900);
            Assert.True(IsSubmenu(wheel));
            Assert.False(wheel.IsBackDwellVisible);   // sobre uma fatia, nada de "voltar"

            wheel.UpdateCursor(OriginX, OriginY);
            Pump(150);
            Assert.True(wheel.IsBackDwellVisible);    // no centro do submenu: o círculo está sendo desenhado
            double partial = wheel.BackDwellArc.StrokeDashOffset;
            Pump(120);
            Assert.True(wheel.BackDwellArc.StrokeDashOffset < partial, "O círculo não avançou com o tempo.");

            HoverConverter(wheel);                    // sai do centro antes de completar
            Pump(50);
            Assert.False(wheel.IsBackDwellVisible);
            Assert.True(IsSubmenu(wheel));

            wheel.UpdateCursor(OriginX, OriginY);
            Pump(800);                                // completa: volta ao menu principal
            Assert.False(IsSubmenu(wheel));
            Assert.False(wheel.IsBackDwellVisible);   // e o círculo não fica para trás
            wheel.Close();
        });
    }

    [Fact]
    public void Voltar_CirculoFicaEntreOMioloEAsFatias()
    {
        RunSta(() =>
        {
            RadialMenuWindow wheel = CreateWheel(dwellMilliseconds: 400);
            HoverConverter(wheel);
            Pump(900);
            wheel.UpdateCursor(OriginX, OriginY);
            Pump(100);

            System.Windows.Rect bounds = wheel.BackDwellArc.Data.Bounds;
            double center = RadialMenuWindow.SizeDip / 2;
            double radius = RadialMenuWindow.BackDwellRadius;

            Assert.InRange(radius - wheel.BackDwellArc.StrokeThickness / 2, 40.5, 58);   // fora do miolo (raio 40)
            Assert.InRange(radius + wheel.BackDwellArc.StrokeThickness / 2, 40, 57.5);   // dentro do furo das fatias (raio 58)
            Assert.Equal(center - radius, bounds.Left, precision: 3);                     // círculo inteiro, centrado
            Assert.Equal(center - radius, bounds.Top, precision: 3);
            Assert.Equal(2 * radius, bounds.Width, precision: 3);
            Assert.Equal(2 * radius, bounds.Height, precision: 3);
            wheel.Close();
        });
    }

    // ------------------------------------------------------------------
    // O programa de baixo não "vê" o mouse enquanto a roda está aberta
    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);

    /// <summary>A janela de nível superior que o Windows considera "sob o mouse" no ponto (a que recebe hover e dicas).</summary>
    private static IntPtr TopLevelAt(int x, int y) => GetAncestor(WindowFromPoint(new NativePoint { X = x, Y = y }), 2 /* GA_ROOT */);

    [Fact]
    public void RodaAberta_SeguraOMouseEmTodoOQuadrado_InclusiveNasAreasVazias()
    {
        // Regressão: a roda deixava o mouse passar (WS_EX_TRANSPARENT + fundo 100% transparente). Com o cursor parado
        // numa opção, o Explorer embaixo achava que ele estava parado sobre um arquivo e mostrava a dica por cima da roda.
        RunSta(() =>
        {
            RadialMenuWindow wheel = CreateWheel(dwellMilliseconds: 650);
            IntPtr hwnd = new WindowInteropHelper(wheel).Handle;
            double scale = RadialMenuWindow.GetMonitorScale(OriginX, OriginY);
            int Px(double dip) => (int)Math.Round(dip * scale);

            Assert.Equal(hwnd, TopLevelAt(OriginX, OriginY));                         // miolo
            Assert.Equal(hwnd, TopLevelAt(OriginX + Px(110), OriginY));               // sobre uma fatia
            Assert.Equal(hwnd, TopLevelAt(OriginX - Px(205), OriginY - Px(205)));     // canto vazio do quadrado (fora do anel)
            Assert.NotEqual(hwnd, TopLevelAt(OriginX + Px(300), OriginY));            // fora da roda: volta a ser o que estiver lá

            wheel.Close();
        });
    }

    [Fact]
    public void RodaFechada_DevolveOMouseParaOQueEstaEmbaixo()
    {
        RunSta(() =>
        {
            RadialMenuWindow wheel = CreateWheel(dwellMilliseconds: 650);
            IntPtr hwnd = new WindowInteropHelper(wheel).Handle;
            Assert.Equal(hwnd, TopLevelAt(OriginX, OriginY));

            wheel.Dismiss(OriginX, OriginY);
            Pump(50);

            Assert.NotEqual(hwnd, TopLevelAt(OriginX, OriginY));
            wheel.Close();
        });
    }

    [Fact]
    public void Roda_NaoRoubaOFocoENaoAtravessaOMouse()
    {
        RunSta(() =>
        {
            RadialMenuWindow wheel = CreateWheel(dwellMilliseconds: 650);
            int ex = GetWindowLong(new WindowInteropHelper(wheel).Handle, -20 /* GWL_EXSTYLE */);

            Assert.True((ex & 0x08000000) != 0, "WS_EX_NOACTIVATE: a roda não pode roubar o foco do programa de baixo");
            Assert.True((ex & 0x00000080) != 0, "WS_EX_TOOLWINDOW: sem botão na barra de tarefas nem no Alt+Tab");
            Assert.True((ex & 0x00000020) == 0, "WS_EX_TRANSPARENT faria o mouse atravessar a roda");

            // O fundo que segura o mouse precisa ser invisível: opacidade de no máximo 2/255.
            var background = Assert.IsType<SolidColorBrush>(wheel.Background);
            Assert.InRange(background.Color.A, (byte)1, (byte)2);

            wheel.Close();
        });
    }

    // ------------------------------------------------------------------
    // Rótulos
    // ------------------------------------------------------------------

    [Fact]
    public void LabelMaxWidth_DiminuiComMaisFatiasEFicaNosLimites()
    {
        double previous = double.MaxValue;
        for (int count = 2; count <= RadialGeometry.MaxSlices; count++)
        {
            double width = RadialMenuWindow.LabelMaxWidth(count);
            Assert.InRange(width, 40, 92);
            Assert.True(width <= previous, $"{count} fatias: {width} > {previous}");
            previous = width;
        }

        Assert.InRange(RadialMenuWindow.LabelMaxWidth(0), 40, 92);   // entrada inválida não quebra
    }
}
