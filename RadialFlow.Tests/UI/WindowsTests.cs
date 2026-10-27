using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using RadialFlow.Config;
using RadialFlow.Services;
using RadialFlow.Tests.TestSupport;
using RadialFlow.UI;
using Xunit;

namespace RadialFlow.Tests;

/// <summary>
/// Janelas do app: tema, scroll das Configurações e diálogos. As janelas abrem FORA da tela (X = -20000) e as
/// configurações vêm de um objeto em memória (nada do arquivo real do usuário é lido ou gravado).
/// </summary>
[Collection("UiWindows")]
public sealed class WindowsTests : IDisposable
{
    public WindowsTests() => SettingsService.OverrideCurrentForTests(AppSettings.CreateDefault());

    public void Dispose()
    {
        SettingsService.OverrideCurrentForTests(null);
        Dialogs.BeforeShow = null;
    }

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

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static SettingsWindow OpenSettings(double? height = null)
    {
        var window = new SettingsWindow(null!, null!)
        {
            Left = -20000, Top = 100, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual
        };
        if (height is { } h) window.Height = h;
        window.Show();
        Pump(300);
        return window;
    }

    private static T Find<T>(FrameworkElement root, string name) where T : class =>
        (root.FindName(name) as T) ?? throw new InvalidOperationException($"'{name}' não encontrado.");

    // ------------------------------------------------------------------
    // Aba Conversores
    // ------------------------------------------------------------------

    [Fact]
    public void Conversores_UmaLinhaPorPrograma_ComTamanhoEVersao()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            var tabs = Find<TabControl>(window, "MainTabs");
            tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "Conversores");
            WaitConvertersChecked(window);

            var list = Find<StackPanel>(window, "ConverterList");
            Assert.Equal(Services.ConverterPackages.All.Count, list.Children.Count);

            string allText = string.Join("\n", FindVisualChildren<TextBlock>(list).Select(t => t.Text));
            foreach (var package in Services.ConverterPackages.All)
            {
                Assert.Contains(package.Name, allText);
                Assert.Contains(package.Version, allText);
                Assert.Contains(package.SizeText, allText);
            }

            Assert.False(string.IsNullOrWhiteSpace(Find<TextBlock>(window, "ConverterStatus").Text));
            window.Close();
        });
    }

    [RequiresConverterInstallBlockedFact]
    public void Conversores_SemComoInstalarDaqui_NaoDeixaInstalarEDizPorque()
    {
        // Os testes rodam de bin\ (pasta do usuário): é o caso da versão portátil fora de C:\Program Files.
        string reason = Services.ConverterInstaller.CheckCanInstall()!;

        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            WaitConvertersChecked(window);

            Assert.False(Find<Button>(window, "BtnInstallConverters").IsEnabled);
            Assert.All(FindVisualChildren<CheckBox>(Find<StackPanel>(window, "ConverterList")), c => Assert.False(c.IsEnabled));
            Assert.Equal(reason, Find<TextBlock>(window, "ConverterStatus").Text);
            window.Close();
        });
    }

    /// <summary>A verificação dos conversores roda fora da UI (disco e permissões): espera ela terminar, até 20 s.</summary>
    private static void WaitConvertersChecked(SettingsWindow window)
    {
        var status = Find<TextBlock>(window, "ConverterStatus");
        var refresh = Find<Button>(window, "BtnRefreshConverters");
        DateTime limit = DateTime.UtcNow.AddSeconds(20);
        do Pump(100);
        while (!refresh.IsEnabled && DateTime.UtcNow < limit);
        Assert.True(refresh.IsEnabled, $"A verificação não terminou: '{status.Text}'.");
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        // Lógica (não visual): os cartões da aba podem não ter template aplicado se a aba não foi aberta.
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            if (node is T match) yield return match;
            foreach (T nested in FindVisualChildren<T>(node)) yield return nested;
        }
    }

    // ------------------------------------------------------------------
    // Scroll das Configurações (o bug: conteúdo cortado, sem barra)
    // ------------------------------------------------------------------

    [Fact]
    public void Configuracoes_AbaGeral_RolaQuandoOConteudoNaoCabe()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings(height: 520);
            ScrollViewer scroll = Find<ScrollViewer>(window, "GeneralScroll");

            Assert.True(scroll.ExtentHeight > scroll.ViewportHeight, "o conteúdo deveria ser maior que a área visível");
            Assert.True(scroll.ScrollableHeight > 0);
            Assert.Equal(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);

            // A viewport tem altura FINITA (limitada pela janela): é isso que faz o ScrollViewer rolar.
            Assert.True(scroll.ViewportHeight < window.ActualHeight);

            window.Close();
        });
    }

    [Fact]
    public void Configuracoes_AbaAparencia_ChegaAoUltimoCartao()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings(height: 520);
            var tabs = Find<TabControl>(window, "MainTabs");
            tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "Aparência");
            Pump(300);

            ScrollViewer scroll = Find<ScrollViewer>(window, "AppearanceScroll");
            ComboBox language = Find<ComboBox>(window, "CmbLanguage");   // último cartão

            scroll.ScrollToEnd();
            Pump(200);

            Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset, precision: 1);

            // O último cartão fica totalmente dentro da área visível depois de rolar até o fim.
            Rect inViewport = language.TransformToAncestor(scroll).TransformBounds(new Rect(0, 0, language.ActualWidth, language.ActualHeight));
            Assert.True(inViewport.Top >= 0 && inViewport.Bottom <= scroll.ViewportHeight + 1, $"idioma em {inViewport} / viewport {scroll.ViewportHeight}");

            window.Close();
        });
    }

    [Fact]
    public void Configuracoes_RolaComARodinhaDoMouse()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings(height: 520);
            ScrollViewer scroll = Find<ScrollViewer>(window, "GeneralScroll");
            Slider slider = Find<Slider>(window, "SliderDeadzone");   // sob o cursor da rodinha

            double before = scroll.VerticalOffset;
            var wheel = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -240)
            {
                RoutedEvent = UIElement.MouseWheelEvent
            };
            slider.RaiseEvent(wheel);
            Pump(200);

            Assert.True(scroll.VerticalOffset > before, "a rodinha sobre o slider deveria rolar a aba");
            window.Close();
        });
    }

    [Fact]
    public void Configuracoes_JanelaMenor_AindaRola()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings(height: 520);   // altura mínima
            ScrollViewer scroll = Find<ScrollViewer>(window, "GeneralScroll");

            Assert.True(scroll.ScrollableHeight > 0);
            Assert.Equal(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);
            window.Close();
        });
    }

    [Fact]
    public void Configuracoes_ListasOcupamOEspacoEmVezDeCrescerSemLimite()
    {
        RunSta(() =>
        {
            // 60 entradas: a lista rola por dentro, o botão "Adicionar" continua visível abaixo dela.
            var settings = AppSettings.CreateDefault();
            for (int i = 0; i < 60; i++)
                settings.Blacklist.Add(new RadialFlow.Input.ForegroundBlacklist.BlacklistEntry($"app{i}", $"Aplicativo {i}"));
            SettingsService.OverrideCurrentForTests(settings);

            SettingsWindow window = OpenSettings();
            var tabs = Find<TabControl>(window, "MainTabs");
            tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "Blacklist");
            Pump(300);

            Button add = Find<Button>(window, "BtnAddBlacklist");
            Rect bounds = add.TransformToAncestor(window).TransformBounds(new Rect(0, 0, add.ActualWidth, add.ActualHeight));
            Assert.True(bounds.Bottom <= window.ActualHeight, "o botão ficou fora da janela");

            window.Close();
        });
    }

    // ------------------------------------------------------------------
    // Tema nas janelas
    // ------------------------------------------------------------------

    [Fact]
    public void Configuracoes_UsaOTemaDoApp_SemCoresFixasAntigas()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();

            var background = Assert.IsType<SolidColorBrush>(window.Background);
            Assert.NotEqual(Color.FromRgb(0x13, 0x17, 0x22), background.Color);   // o antigo #131722 fixo
            Assert.True(window.TryFindResource("AccentBrush") is SolidColorBrush);
            Assert.True(window.TryFindResource("CardBrush") is SolidColorBrush);
            Assert.True(window.TryFindResource("PrimaryButton") is Style);
            window.Close();
        });
    }

    // ------------------------------------------------------------------
    // Contrato de template das abas (automação)
    // ------------------------------------------------------------------

    [Fact]
    public void Configuracoes_TemplateExpoeOConteudoDaAbaSelecionada()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            var tabs = Find<TabControl>(window, "MainTabs");
            tabs.ApplyTemplate();

            var host = tabs.Template.FindName("PART_SelectedContentHost", tabs) as ContentPresenter;
            Assert.NotNull(host);
            Assert.Same(tabs.SelectedContent, host!.Content);

            tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "Aparência");
            Pump(100);
            Assert.Same(tabs.SelectedContent, host.Content);
            window.Close();
        });
    }

    [Fact]
    public void Abas_TemplateCompartilhado_TambemExpoeOConteudoSelecionado()
    {
        RunSta(() =>
        {
            var window = new Window
            {
                Left = -20000, Top = 100, Width = 320, Height = 240, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            UiTheme.Install(window);

            var tabs = new TabControl();
            tabs.Items.Add(new TabItem { Header = "Um", Content = new Border() });
            tabs.Items.Add(new TabItem { Header = "Dois", Content = new Border() });
            window.Content = tabs;
            window.Show();
            Pump(100);

            var host = tabs.Template.FindName("PART_SelectedContentHost", tabs) as ContentPresenter;
            Assert.NotNull(host);
            Assert.Same(tabs.SelectedContent, host!.Content);

            tabs.SelectedIndex = 1;
            Pump(50);
            Assert.Same(tabs.SelectedContent, host.Content);
            window.Close();
        });
    }

    [Fact]
    public void Tema_RefreshTrocaAsCoresDaJanelaAberta()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            Color before = ((SolidColorBrush)window.FindResource("AccentBrush")).Color;

            UiTheme.Refresh(window, RadialTheme.Create(light: false, RadialTheme.ResolveAccent("Orange")));

            Color after = ((SolidColorBrush)window.FindResource("AccentBrush")).Color;
            var gradient = Assert.IsType<LinearGradientBrush>(window.FindResource("AccentGradientBrush"));
            Assert.NotEqual(before, after);
            Assert.True(after.R > after.B, "laranja deveria ter mais vermelho que azul");
            Assert.True(gradient.GradientStops[0].Color.R > gradient.GradientStops[0].Color.B,
                "o gradiente também deveria acompanhar o novo destaque");
            window.Close();
        });
    }

    public static TheoryData<bool> LightAndDark => new() { false, true };

    [Theory]
    [MemberData(nameof(LightAndDark))]
    public void Paleta_TextoTemContrasteSobreCadaSuperficie(bool light)
    {
        static Color Over(Color veil, Color background)
        {
            double alpha = veil.A / 255.0;
            return Color.FromRgb(
                (byte)Math.Round(veil.R * alpha + background.R * (1 - alpha)),
                (byte)Math.Round(veil.G * alpha + background.G * (1 - alpha)),
                (byte)Math.Round(veil.B * alpha + background.B * (1 - alpha)));
        }

        foreach (var (key, _, color) in RadialTheme.AccentChoices)
        {
            UiPalette p = UiPalette.Create(RadialTheme.Create(light, color ?? Color.FromRgb(0x00, 0x78, 0xD4)));
            Color softOnCard = Over(p.AccentSoft, p.CardTop);
            // O vidro pode cobrir qualquer imagem: o pior caso é preto (tema claro) ou branco (escuro).
            Color glassWorst = Over(p.GlassSurface, light ? Colors.Black : Colors.White);

            Assert.True(ColorMath.Contrast(p.Text, p.Surface) >= 7, $"{key}: texto/janela");
            Assert.True(ColorMath.Contrast(p.Text, p.Card) >= 7, $"{key}: texto/cartão");
            Assert.True(ColorMath.Contrast(p.Text, p.CardTop) >= 7, $"{key}: texto/topo do cartão");
            Assert.True(ColorMath.Contrast(p.Text, softOnCard) >= 7, $"{key}: texto/item selecionado");
            Assert.True(ColorMath.Contrast(p.Text, glassWorst) >= 7, $"{key}: texto/vidro");
            Assert.True(ColorMath.Contrast(p.AccentOnSoft, softOnCard) >= RadialTheme.MinTextContrast, $"{key}: destaque/pílula");
            Assert.True(ColorMath.Contrast(p.TextDim, p.Card) >= RadialTheme.MinTextContrast, $"{key}: texto secundário/cartão");
            Assert.True(ColorMath.Contrast(p.TextDim, p.Surface) >= RadialTheme.MinTextContrast, $"{key}: texto secundário/janela");
            Assert.True(ColorMath.Contrast(p.TextDim, glassWorst) >= RadialTheme.MinTextContrast, $"{key}: texto secundário/vidro");
            Assert.True(ColorMath.Contrast(p.AccentText, p.Card) >= RadialTheme.MinTextContrast, $"{key}: valor em destaque/cartão");
            Assert.True(ColorMath.Contrast(p.AccentOnGlass, glassWorst) >= RadialTheme.MinTextContrast, $"{key}: destaque/vidro");
            Assert.True(ColorMath.Contrast(p.DangerOnGlass, glassWorst) >= RadialTheme.MinTextContrast, $"{key}: erro/vidro");
            Assert.True(ColorMath.Contrast(p.WarningOnGlass, glassWorst) >= RadialTheme.MinTextContrast, $"{key}: aviso/vidro");
            Assert.True(ColorMath.Contrast(p.AccentInk, p.Accent) >= RadialTheme.MinTextContrast, $"{key}: texto do botão principal");
            Assert.True(ColorMath.Contrast(p.AccentInk, p.AccentHover) >= RadialTheme.MinTextContrast, $"{key}: texto no topo do gradiente");
            Assert.True(ColorMath.Contrast(p.AccentInk, p.AccentPressed) >= RadialTheme.MinTextContrast, $"{key}: texto na base do gradiente");
            Assert.True(ColorMath.Contrast(p.Text, p.Control) >= RadialTheme.MinTextContrast, $"{key}: texto do botão");
            Assert.True(ColorMath.Contrast(p.Danger, p.Surface) >= 3, $"{key}: cor de erro");
        }
    }

    [Fact]
    public void Paleta_ExportaTodasAsChavesUsadasPeloEstilo()
    {
        string xaml = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "UI", "Theme", "Controls.xaml"));
        var keys = UiPalette.Create(RadialTheme.Create(false, Colors.CornflowerBlue)).Entries().Select(e => e.Key + "Brush")
            .Concat(UiTheme.GradientBrushKeys).ToHashSet();

        // Todo {DynamicResource XxxBrush} citado no estilo precisa existir na paleta.
        var used = System.Text.RegularExpressions.Regex.Matches(xaml, @"DynamicResource (\w+Brush)\}")
            .Select(m => m.Groups[1].Value).ToHashSet();

        Assert.NotEmpty(used);
        Assert.All(used, key => Assert.Contains(key, keys));
    }

    // ------------------------------------------------------------------
    // Cor de destaque
    // ------------------------------------------------------------------

    [Fact]
    public void Configuracoes_UmaBolinhaPorCor_ComAsalvaMarcada()
    {
        RunSta(() =>
        {
            var settings = AppSettings.CreateDefault();
            settings.Accent = "Teal";
            SettingsService.OverrideCurrentForTests(settings);

            SettingsWindow window = OpenSettings();
            RadioButton[] chips = Find<Panel>(window, "AccentChips").Children.OfType<RadioButton>().ToArray();

            Assert.Equal(RadialTheme.AccentChoices.Length, chips.Length);
            Assert.Equal("Teal", Assert.Single(chips, c => c.IsChecked == true).Tag);
            Assert.Equal("Verde-água", Find<TextBlock>(window, "AccentName").Text);
            window.Close();
        });
    }

    [Fact]
    public void Configuracoes_EscolherUmaCor_AtualizaOFormularioNaHora()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            Panel chips = Find<Panel>(window, "AccentChips");
            Color before = ((SolidColorBrush)window.FindResource("AccentBrush")).Color;

            chips.Children.OfType<RadioButton>().First(c => (string)c.Tag == "Pink").IsChecked = true;
            Pump(100);

            Assert.NotEqual(before, ((SolidColorBrush)window.FindResource("AccentBrush")).Color);
            Assert.Equal("Rosa", Find<TextBlock>(window, "AccentName").Text);

            // Volta à cor original: fechar com alteração pendente agora pede confirmação.
            chips.Children.OfType<RadioButton>().First(c => (string)c.Tag == "Blue").IsChecked = true;
            Pump(100);
            window.Close();
        });
    }

    [Fact]
    public void Configuracoes_CorSalvaDesconhecida_CaiNoAzul()
    {
        RunSta(() =>
        {
            var settings = AppSettings.CreateDefault();
            settings.Accent = "cor-que-nao-existe";
            SettingsService.OverrideCurrentForTests(settings);

            SettingsWindow window = OpenSettings();
            RadioButton[] chips = Find<Panel>(window, "AccentChips").Children.OfType<RadioButton>().ToArray();

            Assert.Equal("Blue", Assert.Single(chips, c => c.IsChecked == true).Tag);
            window.Close();
        });
    }

    // ------------------------------------------------------------------
    // Blacklist
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("blender", "blender")]
    [InlineData("  Blender.EXE ", "Blender")]
    [InlineData("acad.exe", "acad")]
    [InlineData("my.app.exe", "my.app")]
    [InlineData(".exe", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void NormalizeProcessName_TiraEspacosEExtensao(string? input, string expected)
    {
        Assert.Equal(expected, SettingsWindow.NormalizeProcessName(input));
    }

    // ------------------------------------------------------------------
    // Outras janelas
    // ------------------------------------------------------------------

    [Fact]
    public void Progresso_AbreComTemaEMostraAcoesCertasEmCadaEstado()
    {
        RunSta(() =>
        {
            var window = new ProgressWindow("Compactando…", @"C:\Temp\saida.zip")
            {
                Left = -20000, Top = 100, WindowStartupLocation = WindowStartupLocation.Manual
            };
            window.Show();
            Pump(200);

            Button open = Find<Button>(window, "BtnOpenFolder");
            Button cancel = Find<Button>(window, "BtnCancel");
            Assert.Equal(Visibility.Collapsed, open.Visibility);
            Assert.Equal("Cancelar", cancel.Content);
            Assert.True(window.TryFindResource("AccentBrush") is SolidColorBrush);

            window.Report(new ProgressReport(100, "Pronto"));
            Pump(200);
            Assert.Equal(Visibility.Visible, open.Visibility);
            Assert.Equal("Fechar", cancel.Content);

            window.Close();
        });
    }

    [Fact]
    public void Progresso_Erro_EscondeAbrirPastaEUsaACorDeErro()
    {
        RunSta(() =>
        {
            var window = new ProgressWindow("Convertendo…", @"C:\Temp\foto.webp")
            {
                Left = -20000, Top = 100, WindowStartupLocation = WindowStartupLocation.Manual
            };
            window.Show();
            Pump(150);

            window.MarkFailed("Falha na conversão: código 183.");
            window.Report(new ProgressReport(100, "relatório atrasado que não pode sobrescrever o erro"));
            Pump(250);

            Assert.Equal("Erro", Find<TextBlock>(window, "TxtPercent").Text);
            Assert.StartsWith("Falha na conversão", Find<TextBlock>(window, "TxtStatus").Text);
            Assert.Equal(Visibility.Collapsed, Find<Button>(window, "BtnOpenFolder").Visibility);
            Assert.Equal("Fechar", Find<Button>(window, "BtnCancel").Content);
            window.Close();
        });
    }

    [Fact]
    public void Progresso_LoteComFalhas_MostraAtencaoENaoEhSobrescrito()
    {
        RunSta(() =>
        {
            var window = new ProgressWindow("Convertendo 3 arquivos…", @"C:\Temp\foto.webp")
            {
                Left = -20000, Top = 100, WindowStartupLocation = WindowStartupLocation.Manual
            };
            window.Show();
            Pump(150);

            window.MarkCompleted("2 de 3 arquivos convertidos; 1 com erro.", warning: true);
            window.Report(new ProgressReport(40, "relatório atrasado"));
            Pump(250);

            Assert.Equal("Atenção", Find<TextBlock>(window, "TxtPercent").Text);
            Assert.Equal("2 de 3 arquivos convertidos; 1 com erro.", Find<TextBlock>(window, "TxtStatus").Text);
            Assert.Equal(Visibility.Visible, Find<Button>(window, "BtnOpenFolder").Visibility);
            Assert.Equal("Fechar", Find<Button>(window, "BtnCancel").Content);
            window.Close();
        });
    }

    [Fact]
    public void Vidro_JanelasForaDaTela_FicamComFundoOpaco()
    {
        RunSta(() =>
        {
            var progress = new ProgressWindow("Teste")
            {
                Left = -20000, Top = 100, WindowStartupLocation = WindowStartupLocation.Manual
            };
            progress.Show();
            Pump(200);
            AssertOpaqueGlass(progress);
            progress.Close();

            string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rf-{Guid.NewGuid():N}.png");
            System.IO.File.WriteAllBytes(file, []);
            try
            {
                var conversion = new ConversionWindow(new[] { file })
                {
                    Left = -20000,
                    Top = 100,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                conversion.Show();
                Pump(300);
                AssertOpaqueGlass(conversion);
                conversion.Close();
            }
            finally
            {
                System.IO.File.Delete(file);
            }

            bool checkedDialog = false;
            OnShown(window =>
            {
                AssertOpaqueGlass(window);
                checkedDialog = true;
            });
            Dialogs.Message(null, "Título", "Mensagem");
            Assert.True(checkedDialog);
        });
    }

    private static void AssertOpaqueGlass(Window window)
    {
        Border imageHost, tintHost;
        if (window.FindName("GlassImageHost") is Border named)
        {
            imageHost = named;
            tintHost = Find<Border>(window, "GlassTint");
        }
        else
        {
            // Diálogos montam as camadas em código, sem escopo de nomes.
            var root = Assert.IsType<Grid>(window.Content);
            imageHost = Assert.IsType<Border>(root.Children[0]);
            tintHost = Assert.IsType<Border>(root.Children[1]);
        }

        Assert.Null(imageHost.Background);
        var tint = Assert.IsType<SolidColorBrush>(tintHost.Background);
        Assert.Equal(((SolidColorBrush)window.FindResource("SurfaceBrush")).Color, tint.Color);
    }

    [Fact]
    public void Movimento_DuracaoCurta_Padronizada()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(110), Motion.Fast);
    }

    [Fact]
    public void Entrada_SemAnimacao_PainelJaFicaNoLugar()
    {
        RunSta(() =>
        {
            var window = OffscreenWindow(out Grid root);
            Motion.AttachEntrance(window, root, animate: false);
            Assert.Equal(1, root.Opacity);
            Assert.Null(root.RenderTransform as ScaleTransform);

            window.Show();
            Pump(200);
            Assert.Equal(1, root.Opacity);
            Assert.Null(root.RenderTransform as ScaleTransform);
            window.Close();
        });
    }

    [Fact]
    public void Entrada_ComAnimacao_VaiDe98PorCentoAoCheio()
    {
        RunSta(() =>
        {
            var window = OffscreenWindow(out Grid root);
            Motion.AttachEntrance(window, root, animate: true);

            Assert.Equal(0, root.Opacity);
            var scale = Assert.IsType<ScaleTransform>(root.RenderTransform);
            Assert.Equal(0.98, scale.ScaleX, 3);
            Assert.Equal(0.98, scale.ScaleY, 3);

            window.Show();
            Pump(350);

            Assert.Equal(1, root.Opacity);
            Assert.Equal(1, scale.ScaleX, 3);
            Assert.Equal(1, scale.ScaleY, 3);
            window.Close();
        });
    }

    private static Window OffscreenWindow(out Grid root)
    {
        root = new Grid();
        var window = new Window
        {
            Width = 200,
            Height = 120,
            Left = -20000,
            Top = 100,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = root
        };
        return window;
    }

    [Fact]
    public void Conversao_ComboMostraONomeDoFormato_NaoOTipo()
    {
        RunSta(() =>
        {
            string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rf-{Guid.NewGuid():N}.png");
            System.IO.File.WriteAllBytes(file, []);
            try
            {
                var window = new ConversionWindow(new[] { file }) { Left = -20000, Top = 100, WindowStartupLocation = WindowStartupLocation.Manual };
                window.Show();
                Pump(300);

                ComboBox combo = Find<ComboBox>(window, "CmbTargetFormat");
                Assert.NotEmpty(combo.Items);   // PNG sempre converte pelo próprio Windows

                var shown = (combo.Template.FindName("PART_Popup", combo) as System.Windows.Controls.Primitives.Popup);
                Assert.NotNull(shown);
                Assert.Equal("DisplayName", combo.DisplayMemberPath);
                Assert.False(string.IsNullOrWhiteSpace(((ConversionOption)combo.SelectedItem).DisplayName));
                window.Close();
            }
            finally
            {
                System.IO.File.Delete(file);
            }
        });
    }

    [Fact]
    public void Conversao_SelecaoMista_AvisaOArquivoQueSeraIgnorado()
    {
        RunSta(() =>
        {
            string jpg = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rf-{Guid.NewGuid():N}.jpg");
            string png = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rf-{Guid.NewGuid():N}.png");
            System.IO.File.WriteAllBytes(jpg, []);
            System.IO.File.WriteAllBytes(png, []);
            try
            {
                var window = new ConversionWindow(new[] { jpg, png })
                {
                    Left = -20000,
                    Top = 100,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                window.Show();
                Pump(300);

                ComboBox combo = Find<ComboBox>(window, "CmbTargetFormat");
                var option = combo.Items.OfType<ConversionOption>().Single(o => o.TargetFormat == "jpg");
                combo.SelectedItem = option;
                Pump(150);

                TextBlock note = Find<TextBlock>(window, "TxtBatchNote");
                Assert.Equal(Visibility.Visible, note.Visibility);
                Assert.Contains("1 arquivo já está em JPG", note.Text);
                window.Close();
            }
            finally
            {
                System.IO.File.Delete(jpg);
                System.IO.File.Delete(png);
            }
        });
    }

    // ------------------------------------------------------------------
    // Diálogos
    // ------------------------------------------------------------------

    /// <summary>Roda o diálogo e, logo que ele aparece, executa <paramref name="interact"/>; o diálogo é fechado ao final.</summary>
    private static void OnShown(Action<Window> interact) =>
        Dialogs.BeforeShow = window =>
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = 100;
            window.ContentRendered += (_, _) =>
            {
                interact(window);
                if (window.IsVisible) window.Close();
            };
        };

    private static IEnumerable<Button> ButtonsOf(Window window) => Descendants(window).OfType<Button>();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject deeper in Descendants(child)) yield return deeper;
        }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    [Fact]
    public void Dialogo_Choose_DevolveOIndiceDoBotaoClicado()
    {
        RunSta(() =>
        {
            OnShown(w => Click(ButtonsOf(w).First(b => (string)b.Content == "Manter os dois")));

            int answer = Dialogs.Choose(null, "Mover", "Já existe.", DialogIcon.Warning,
                ["Parar", "Manter os dois", "Substituir"], defaultIndex: 1, cancelIndex: 0);

            Assert.Equal(1, answer);
        });
    }

    [Fact]
    public void Dialogo_Choose_FecharSemClicar_ValeCancelar()
    {
        RunSta(() =>
        {
            OnShown(_ => { });   // fecha sem clicar em nada

            int answer = Dialogs.Choose(null, "Mover", "Já existe.", DialogIcon.Warning,
                ["Parar", "Manter os dois", "Substituir"], defaultIndex: 1, cancelIndex: 0);

            Assert.Equal(0, answer);   // o índice de cancelamento, nunca "Substituir"
        });
    }

    [Fact]
    public void Dialogo_Choose_BotaoPadraoEOPrincipal_EEscCancela()
    {
        RunSta(() =>
        {
            bool checkedDefaults = false;
            OnShown(w =>
            {
                Button[] buttons = ButtonsOf(w).ToArray();
                Assert.Single(buttons, b => b.IsDefault);
                Assert.Equal("Manter os dois", buttons.Single(b => b.IsDefault).Content);
                Assert.Equal("Parar", buttons.Single(b => b.IsCancel).Content);
                checkedDefaults = true;
            });

            Dialogs.Choose(null, "Mover", "Já existe.", DialogIcon.Warning, ["Parar", "Manter os dois", "Substituir"], 1, 0);

            Assert.True(checkedDefaults);
        });
    }

    [Fact]
    public void Dialogo_Confirm_AcaoDestrutiva_TemOCancelarComoPadrao()
    {
        RunSta(() =>
        {
            Button? defaultButton = null;
            OnShown(w => defaultButton = ButtonsOf(w).Single(b => b.IsDefault));

            bool confirmed = Dialogs.Confirm(null, "Limpar", "Apagar tudo?", "Apagar tudo", "Cancelar", DialogIcon.Warning, defaultIsConfirm: false);

            Assert.False(confirmed);
            Assert.Equal("Cancelar", defaultButton!.Content);
        });
    }

    [Fact]
    public void Dialogo_Prompt_ValidacaoInline_MantemAbertoEMostraOMotivo()
    {
        RunSta(() =>
        {
            string? shownError = null;
            bool stillOpenAfterInvalid = false;
            OnShown(w =>
            {
                TextBox box = Descendants(w).OfType<TextBox>().First();
                box.Text = "CON";
                Click(ButtonsOf(w).Single(b => b.IsDefault));
                Pump(100);

                stillOpenAfterInvalid = w.IsVisible;
                shownError = Descendants(w).OfType<TextBlock>().FirstOrDefault(t => t.Visibility == Visibility.Visible && t.Text.Contains("reservado"))?.Text;
            });

            string? result = Dialogs.PromptText(null, "Renomear", "Novo nome", "arquivo.txt",
                text => FileNaming.TryNormalizeFileName(text, out _, out string? error) ? null : error);

            Assert.Null(result);   // fechado sem confirmar
            Assert.True(stillOpenAfterInvalid, "o diálogo deveria continuar aberto após um nome inválido");
            Assert.Contains("reservado", shownError);
        });
    }

    [Fact]
    public void Dialogo_Prompt_NomeValido_DevolveOTexto()
    {
        RunSta(() =>
        {
            OnShown(w =>
            {
                Descendants(w).OfType<TextBox>().First().Text = "novo nome.txt";
                Click(ButtonsOf(w).Single(b => b.IsDefault));
            });

            string? result = Dialogs.PromptText(null, "Renomear", "Novo nome", "arquivo.txt",
                text => FileNaming.TryNormalizeFileName(text, out _, out string? error) ? null : error);

            Assert.Equal("novo nome.txt", result);
        });
    }

    [Fact]
    public void Dialogo_Prompt_SelecionaSoONomeSemExtensao()
    {
        RunSta(() =>
        {
            string selected = "";
            OnShown(w => selected = Descendants(w).OfType<TextBox>().First().SelectedText);

            Dialogs.PromptText(null, "Renomear", "Novo nome", "relatório.docx", selectLength: "relatório".Length);

            Assert.Equal("relatório", selected);
        });
    }

    [Fact]
    public void Dialogo_PromptPair_ValidaOsDoisCamposEDevolveOsDois()
    {
        RunSta(() =>
        {
            OnShown(w =>
            {
                TextBox[] boxes = Descendants(w).OfType<TextBox>().ToArray();
                boxes[0].Text = "blender";
                boxes[1].Text = "Blender 3D";
                Click(ButtonsOf(w).Single(b => b.IsDefault));
            });

            var result = Dialogs.PromptPair(null, "Adicionar", "Processo", "", "Descrição", "",
                (process, _) => string.IsNullOrWhiteSpace(process) ? "Informe o processo." : null);

            Assert.Equal(("blender", "Blender 3D"), result);
        });
    }

    [Fact]
    public void Dialogo_Choose_SemBotoes_Lanca()
    {
        Assert.Throws<ArgumentException>(() => Dialogs.Choose(null, "x", "y", DialogIcon.None, []));
    }
}
