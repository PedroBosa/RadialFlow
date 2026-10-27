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
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
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

    // ------------------------------------------------------------------
    // Teclado: setas, Enter, Esc e atalhos 1–8
    // ------------------------------------------------------------------

    [Fact]
    public void Teclado_SetasCirculamPelasFatias()
    {
        RunSta(() =>
        {
            RadialMenuWindow wheel = CreateWheel(dwellMilliseconds: 650);

            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Next);
            Assert.True(wheel.IsKeyboardSelectionActive);
            Assert.Equal(0, wheel.KeyboardSelection);

            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Next);
            Assert.Equal(1, wheel.KeyboardSelection);

            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Previous);
            Assert.Equal(0, wheel.KeyboardSelection);

            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Previous);
            Assert.Equal(2, wheel.KeyboardSelection);   // deu a volta

            wheel.Close();
        });
    }

    [Fact]
    public void Teclado_SemSelecao_NextEscolheAPrimeira_EPreviousAUltima()
    {
        RunSta(() =>
        {
            RadialMenuWindow wheel = CreateWheel(dwellMilliseconds: 650);

            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Previous);
            Assert.Equal(2, wheel.KeyboardSelection);
            wheel.Close();

            RadialMenuWindow again = CreateWheel(dwellMilliseconds: 650);
            again.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Next);
            Assert.Equal(0, again.KeyboardSelection);
            again.Close();
        });
    }

    [Fact]
    public void Teclado_EnterNoSubmenuAbre_EEscVoltaComAFatiaDestacada()
    {
        RunSta(() =>
        {
            RadialMenuWindow wheel = CreateWheel(dwellMilliseconds: 650);

            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Next);     // Comprimir
            Assert.Contains("Enter executa", wheel.HintText.Text);
            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Next);     // Converter
            Assert.Contains("Enter abre", wheel.HintText.Text);

            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Activate);
            Assert.True(IsSubmenu(wheel));                        // abriu o submenu
            Assert.Equal(0, wheel.KeyboardSelection);             // foco na primeira opção
            Assert.Contains("Descrição de PNG", wheel.HintText.Text);

            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Back);
            Assert.False(IsSubmenu(wheel));                       // voltou ao menu principal
            Assert.Equal(1, wheel.KeyboardSelection);             // destaque de volta em Converter
            Assert.Contains("Descrição de Converter", wheel.HintText.Text);

            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Activate);
            Assert.True(IsSubmenu(wheel));                        // Enter reabre o submenu

            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Back);
            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Back);
            Assert.False(wheel.IsVisible);                        // no menu principal, Esc fecha
            wheel.Close();
        });
    }

    [Fact]
    public void Teclado_EnterEmFatiaSemSubmenu_ExecutaEFecha()
    {
        RunSta(() =>
        {
            RadialMenuWindow wheel = CreateWheel(dwellMilliseconds: 650);
            RadialSlice? executed = null;
            wheel.SliceActivated += slice => executed = slice;

            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Next);     // Comprimir
            Assert.Null(executed);                                                       // só destacou
            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Activate);

            Assert.Equal("Comprimir", executed?.Label);
            Assert.False(wheel.IsVisible);
            wheel.Close();
        });
    }

    [Fact]
    public void Teclado_NumerosEscolhemPorPosicao_ESubmenuAbreNaHora()
    {
        RunSta(() =>
        {
            RadialMenuWindow wheel = CreateWheel(dwellMilliseconds: 650);
            RadialSlice? executed = null;
            wheel.SliceActivated += slice => executed = slice;

            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Select2);   // Converter
            Assert.True(IsSubmenu(wheel));
            Assert.Equal(0, wheel.KeyboardSelection);

            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Select3);   // WEBP
            Assert.Equal("WEBP", executed?.Label);
            Assert.False(wheel.IsVisible);
            wheel.Close();
        });
    }

    [Fact]
    public void Teclado_NumeroForaDoIntervalo_EhIgnorado()
    {
        RunSta(() =>
        {
            RadialMenuWindow wheel = CreateWheel(dwellMilliseconds: 650);
            bool executed = false;
            wheel.SliceActivated += _ => executed = true;

            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Select5);   // só há 3 fatias

            Assert.True(wheel.IsVisible);
            Assert.False(wheel.IsKeyboardSelectionActive);
            Assert.False(executed);
            wheel.Close();
        });
    }

    [Fact]
    public void Teclado_SoltarOBotaoDireito_RespeitaASelecaoDoTeclado()
    {
        RunSta(() =>
        {
            RadialMenuWindow wheel = CreateWheel(dwellMilliseconds: 650);

            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Next);     // Comprimir
            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Next);     // Converter
            RadialSlice? released = wheel.Dismiss(OriginX, OriginY);   // centro: pelo mouse, nada seria executado

            Assert.Equal("Converter", released?.Label);
            wheel.Close();
        });
    }

    [Fact]
    public void Teclado_EscNoMenuPrincipal_FechaSemExecutar()
    {
        RunSta(() =>
        {
            RadialMenuWindow wheel = CreateWheel(dwellMilliseconds: 650);
            bool executed = false;
            wheel.SliceActivated += _ => executed = true;

            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Next);
            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Back);

            Assert.False(wheel.IsVisible);
            Assert.False(executed);
            wheel.Close();
        });
    }

    [Fact]
    public void Teclado_TremorDoMouseMantemASelecao_MovimentoRealDevolveAoMouse()
    {
        RunSta(() =>
        {
            RadialMenuWindow wheel = CreateWheel(dwellMilliseconds: 650);

            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Next);     // Comprimir
            wheel.UpdateCursor(OriginX + 2, OriginY + 1);   // tremor: continua no teclado
            Assert.True(wheel.IsKeyboardSelectionActive);
            Assert.Equal(0, wheel.KeyboardSelection);

            HoverConverter(wheel);                          // movimento real: mouse assume
            Assert.False(wheel.IsKeyboardSelectionActive);
            Assert.Equal(-1, wheel.KeyboardSelection);

            double angle = (-90 + 120) * Math.PI / 180;
            RadialSlice? released = wheel.Dismiss(
                OriginX + (int)(150 * Math.Cos(angle)), OriginY + (int)(150 * Math.Sin(angle)));
            Assert.Equal("Converter", released?.Label);

            wheel.Close();
        });
    }

    [Fact]
    public void Teclado_SelecionarMesmaFatiaDoMouse_CancelaAEsperaDoSubmenu()
    {
        RunSta(() =>
        {
            RadialMenuWindow wheel = CreateWheel(dwellMilliseconds: 400);

            HoverConverter(wheel);                          // mouse sobre Converter: espera começou
            Pump(100);
            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Next);     // Comprimir
            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Next);     // Converter (mesma do mouse)

            Assert.Contains("Enter abre", wheel.HintText.Text);

            Pump(900);                                      // tempo de sobra para a espera antiga
            Assert.False(IsSubmenu(wheel));                 // nada abriu sozinho
            wheel.Close();
        });
    }

    [Fact]
    public void Teclado_EscDepoisDeSubmenuAbertoPeloMouse_DestacaAFatiaDeOrigem()
    {
        RunSta(() =>
        {
            RadialMenuWindow wheel = CreateWheel(dwellMilliseconds: 400);

            HoverConverter(wheel);
            Pump(900);
            Assert.True(IsSubmenu(wheel));

            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Back);

            Assert.False(IsSubmenu(wheel));
            Assert.Equal(1, wheel.KeyboardSelection);
            wheel.Close();
        });
    }

    [Fact]
    public void Teclado_AtalhosMapeiamAsTeclasEsperadas()
    {
        Assert.Equal(14, RadialMenuWindow.Hotkeys.Length);   // 4 setas + Enter + Esc + 8 números

        Assert.Equal(0x26u, RadialMenuWindow.Hotkeys[0].VirtualKey);   // ↑
        Assert.Equal(0x25u, RadialMenuWindow.Hotkeys[1].VirtualKey);   // ←
        Assert.Equal(0x27u, RadialMenuWindow.Hotkeys[2].VirtualKey);   // →
        Assert.Equal(0x28u, RadialMenuWindow.Hotkeys[3].VirtualKey);   // ↓
        Assert.Equal(0x0Du, RadialMenuWindow.Hotkeys[4].VirtualKey);   // Enter
        Assert.Equal(0x1Bu, RadialMenuWindow.Hotkeys[5].VirtualKey);   // Esc
        Assert.Equal(0x31u, RadialMenuWindow.Hotkeys[6].VirtualKey);   // 1
        Assert.Equal(0x38u, RadialMenuWindow.Hotkeys[13].VirtualKey);  // 8

        Assert.True(RadialMenuWindow.TryMapHotkey(1, out var previous));
        Assert.Equal(RadialMenuWindow.RadialKeyboardCommand.Previous, previous);
        Assert.True(RadialMenuWindow.TryMapHotkey(5, out var activate));
        Assert.Equal(RadialMenuWindow.RadialKeyboardCommand.Activate, activate);
        Assert.True(RadialMenuWindow.TryMapHotkey(7, out var select1));
        Assert.Equal(RadialMenuWindow.RadialKeyboardCommand.Select1, select1);
        Assert.True(RadialMenuWindow.TryMapHotkey(14, out var select8));
        Assert.Equal(RadialMenuWindow.RadialKeyboardCommand.Select8, select8);

        Assert.False(RadialMenuWindow.TryMapHotkey(0, out _));
        Assert.False(RadialMenuWindow.TryMapHotkey(15, out _));
    }

    [Fact]
    public void Teclado_WmHotkeyNoHwndDaRoda_ChegaAoComando()
    {
        // Sem RegisterHotKey: a mensagem é enviada direto ao HWND, validando o hook do HwndSource e o mapa de ids.
        RunSta(() =>
        {
            RadialMenuWindow wheel = CreateWheel(dwellMilliseconds: 650);
            IntPtr hwnd = new WindowInteropHelper(wheel).Handle;

            SendMessage(hwnd, 0x0312 /* WM_HOTKEY */, new IntPtr(1), IntPtr.Zero);   // id 1 = Previous

            Assert.True(wheel.IsKeyboardSelectionActive);
            Assert.Equal(2, wheel.KeyboardSelection);   // sem seleção, Previous escolhe a última
            wheel.Close();
        });
    }

    // ------------------------------------------------------------------
    // Paginação: mais de 8 opções
    // ------------------------------------------------------------------

    [Fact]
    public void Paginacao_MenuPrincipalCom12Opcoes_AlcancaAUltima()
    {
        RunSta(() =>
        {
            var leaves = new List<RadialSlice>();
            for (int i = 1; i <= 12; i++) leaves.Add(Slice($"F{i}"));

            var wheel = new RadialMenuWindow { DwellMilliseconds = 650 };
            wheel.Prewarm();
            wheel.Present(OriginX, OriginY, leaves);
            Pump(300);

            RadialSlice? executed = null;
            wheel.SliceActivated += slice => executed = slice;

            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Select8);   // "Mais…"
            Assert.True(IsSubmenu(wheel));
            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Select5);   // F12, na 2ª página

            Assert.Equal("F12", executed?.Label);
            Assert.False(wheel.IsVisible);
            wheel.Close();
        });
    }

    [Fact]
    public void Paginacao_SubmenuCom12Opcoes_AlcancaAUltima()
    {
        RunSta(() =>
        {
            var formats = new List<RadialSlice>();
            for (int i = 1; i <= 12; i++) formats.Add(Slice($"F{i}"));

            var wheel = new RadialMenuWindow { DwellMilliseconds = 650 };
            wheel.Prewarm();
            wheel.Present(OriginX, OriginY, [Slice("Converter", formats), Slice("Renomear")]);
            Pump(300);

            RadialSlice? executed = null;
            wheel.SliceActivated += slice => executed = slice;

            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Select1);   // abre o submenu de 12
            Assert.True(IsSubmenu(wheel));
            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Select8);   // "Mais…"
            wheel.HandleKeyboardCommand(RadialMenuWindow.RadialKeyboardCommand.Select5);   // F12, na 2ª página

            Assert.Equal("F12", executed?.Label);
            Assert.False(wheel.IsVisible);
            wheel.Close();
        });
    }
}
