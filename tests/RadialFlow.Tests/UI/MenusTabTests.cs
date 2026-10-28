using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using RadialFlow.Config;
using RadialFlow.Context;
using RadialFlow.Infrastructure;
using RadialFlow.Tests.TestSupport;
using RadialFlow.UI;
using Xunit;

namespace RadialFlow.Tests;

/// <summary>
/// Aba Menus: perfis, itens, prévia e salvar/cancelar. Nada é gravado no arquivo real do usuário
/// (as configurações vêm de um objeto em memória e o Save é interceptado por SaveOverrideForTests).
/// </summary>
[Collection("UiWindows")]
public sealed class MenusTabTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private AppSettings? _saved;

    public MenusTabTests()
    {
        SettingsService.OverrideCurrentForTests(AppSettings.CreateDefault());
        SettingsService.SaveOverrideForTests = settings => _saved = settings;
    }

    public void Dispose()
    {
        SettingsService.SaveOverrideForTests = null;
        SettingsService.OverrideCurrentForTests(null);
        Dialogs.BeforeShow = null;
        SubmenuEditorWindow.BeforeShowForTests = null;
        RoutineEditorWindow.BeforeShowForTests = null;
        SettingsWindow.ChooseImportPathForTests = null;
        SettingsWindow.ChooseExportPathForTests = null;
        _temp.Dispose();
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

    private static SettingsWindow OpenSettings()
    {
        var window = new SettingsWindow(null!, null!)
        {
            Left = -20000, Top = 100, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual
        };
        window.Show();
        Pump(300);
        return window;
    }

    private static T Find<T>(FrameworkElement root, string name) where T : class =>
        (root.FindName(name) as T) ?? throw new InvalidOperationException($"'{name}' não encontrado.");

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static void SelectTab(SettingsWindow window, string header)
    {
        var tabs = Find<TabControl>(window, "MainTabs");
        tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == header);
    }

    private static void SelectTag(ComboBox combo, object tag) =>
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().Single(i => Equals(i.Tag, tag));

    private static ToggleButton ContextChip(SettingsWindow window, RadialContextKind kind) =>
        Find<WrapPanel>(window, "PnlContexts").Children.OfType<ToggleButton>()
            .Single(c => Equals(c.Tag, kind));

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

    private static void CloseDiscarding(Window window)
    {
        OnShown(w => Click(ButtonsOf(w).Single(b => (string)b.Content == "Descartar")));
        window.Close();
        Assert.False(window.IsVisible);
    }

    /// <summary>Captura o texto do próximo diálogo que contiver o fragmento e o fecha com OK.</summary>
    private static Func<string?> CaptureDialog(string fragment)
    {
        string? shown = null;
        OnShown(w =>
        {
            shown = Descendants(w).OfType<TextBlock>()
                .FirstOrDefault(t => t.Visibility == Visibility.Visible && t.Text.Contains(fragment))?.Text;
            Click(ButtonsOf(w).Single(b => (string)b.Content == "OK"));
        });
        return () => shown;
    }

    private static MenuPreview PreviewOf(SettingsWindow window) =>
        (MenuPreview)Find<Border>(window, "MenuPreviewHost").Child;

    private static DispatcherTimer? PreviewTimerOf(SettingsWindow window) =>
        (DispatcherTimer?)typeof(SettingsWindow)
            .GetField("_previewTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window);

    private static CustomMenuProfile TitleBarProfile(int itemCount)
    {
        RadialAction[] replaced =
        [
            RadialAction.Maximize, RadialAction.SnapRight, RadialAction.Close,
            RadialAction.Transparency, RadialAction.AlwaysOnTop, RadialAction.Minimize, RadialAction.SnapLeft,
        ];

        var profile = new CustomMenuProfile
        {
            Name = "Barra",
            Contexts = { RadialContextKind.TitleBar },
        };
        for (int i = 0; i < itemCount; i++)
        {
            profile.Items.Add(new CustomMenuItem
            {
                Label = $"Item {i + 1}",
                Kind = CustomMenuItemKind.Action,
                Action = i < replaced.Length ? replaced[i] : RadialAction.CopyPath,
            });
        }

        return profile;
    }

    // ------------------------------------------------------------------
    // Aba e perfis
    // ------------------------------------------------------------------

    [Fact]
    public void AbaMenus_FicaEntreFavoritosEConversores()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            TabItem[] tabs = Find<TabControl>(window, "MainTabs").Items.Cast<TabItem>().ToArray();

            Assert.Equal(7, tabs.Length);
            Assert.Equal("Aparência", tabs[1].Header);
            Assert.Equal("Blacklist", tabs[2].Header);
            Assert.Equal("Apps Favoritos", tabs[3].Header);
            Assert.Equal("Menus", tabs[4].Header);
            Assert.Equal("Conversores", tabs[5].Header);
            window.Close();
        });
    }

    [Fact]
    public void Perfis_AdicionarDuplicarRemover_SemTocarNasConfiguracoes()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            var profiles = Find<ListBox>(window, "ListMenuProfiles");
            Assert.Empty(profiles.Items);
            Assert.False(Find<Grid>(window, "PnlProfileEditor").IsEnabled);

            Click(Find<Button>(window, "BtnAddMenuProfile"));
            Assert.Single(profiles.Items);
            var first = (CustomMenuProfile)profiles.Items[0];
            Assert.Equal("Novo menu", first.Name);
            Assert.Equal("Novo menu", Find<TextBox>(window, "TxtProfileName").Text);
            Assert.True(Find<Grid>(window, "PnlProfileEditor").IsEnabled);

            Find<TextBox>(window, "TxtProfileName").Text = "Menu A";
            Click(Find<Button>(window, "BtnAddMenuItem"));
            Find<TextBox>(window, "TxtItemLabel").Text = "Primeiro";
            Assert.Equal("Menu A", first.Name);
            Assert.Equal("Primeiro", Assert.Single(first.Items).Label);

            Click(Find<Button>(window, "BtnDuplicateMenuProfile"));
            Assert.Equal(2, profiles.Items.Count);
            var copy = (CustomMenuProfile)profiles.Items[1];
            Assert.Equal("Menu A (cópia)", copy.Name);
            Assert.NotEqual(first.Id, copy.Id);
            Assert.NotSame(first.Items[0], copy.Items[0]);
            // Ids novos: itens de cópias não são deduplicados quando os dois perfis casam com o contexto.
            Assert.NotEqual(first.Items[0].Id, copy.Items[0].Id);
            Assert.Equal("Primeiro", copy.Items[0].Label);

            Click(Find<Button>(window, "BtnRemoveMenuProfile"));
            Assert.Single(profiles.Items);
            Assert.Equal("Menu A", ((CustomMenuProfile)profiles.Items[0]).Name);

            Click(Find<Button>(window, "BtnRemoveMenuProfile"));
            Assert.Empty(profiles.Items);
            Assert.False(Find<Grid>(window, "PnlProfileEditor").IsEnabled);

            Assert.Empty(SettingsService.Current.CustomProfiles);
            CloseDiscarding(window);
        });
    }

    [Fact]
    public void EstadoVazio_UmUnicoConviteEAcoesSoComSelecao()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            Assert.Equal(Visibility.Collapsed, Find<Grid>(window, "MenusWorkspace").Visibility);
            Assert.Equal(Visibility.Visible, Find<Border>(window, "PnlNoMenus").Visibility);
            Assert.Equal(Visibility.Collapsed, Find<Grid>(window, "PnlProfileActions").Visibility);

            Click(Find<Button>(window, "BtnAddMenuProfile"));
            Assert.Equal(Visibility.Visible, Find<Grid>(window, "MenusWorkspace").Visibility);
            Assert.Equal(Visibility.Collapsed, Find<Border>(window, "PnlNoMenus").Visibility);
            Assert.Equal(Visibility.Visible, Find<Grid>(window, "PnlProfileActions").Visibility);

            Click(Find<Button>(window, "BtnRemoveMenuProfile"));
            Assert.Equal(Visibility.Collapsed, Find<Grid>(window, "MenusWorkspace").Visibility);
            Assert.Equal(Visibility.Visible, Find<Border>(window, "PnlNoMenus").Visibility);

            CloseDiscarding(window);
        });
    }

    [Fact]
    public void EstadoVazio_FocoVaiParaONomeAoCriarEDuplicarEVoltaAoConviteAoExcluir()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            SelectTab(window, "Menus");
            window.Activate();
            Pump(100);

            Click(Find<Button>(window, "BtnAddMenuProfile"));
            Pump(50);
            Assert.True(Find<TextBox>(window, "TxtProfileName").IsKeyboardFocusWithin);

            Click(Find<Button>(window, "BtnDuplicateMenuProfile"));
            Pump(50);
            Assert.True(Find<TextBox>(window, "TxtProfileName").IsKeyboardFocusWithin);

            Click(Find<Button>(window, "BtnRemoveMenuProfile"));
            Pump(50);
            Assert.True(Find<TextBox>(window, "TxtProfileName").IsKeyboardFocusWithin);

            Click(Find<Button>(window, "BtnRemoveMenuProfile"));
            Pump(50);
            Assert.True(Find<Button>(window, "BtnAddMenuProfile").IsKeyboardFocusWithin);

            CloseDiscarding(window);
        });
    }

    [Fact]
    public void Secoes_UmAssuntoPorVezEMenuNovoComecaNosItens()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            Click(Find<Button>(window, "BtnAddMenuProfile"));

            var items = Find<Grid>(window, "PnlSectionItems");
            var application = Find<Grid>(window, "PnlSectionApplication");
            var preview = Find<Grid>(window, "PnlSectionPreview");
            var segItems = Find<RadioButton>(window, "SegItems");
            var segApplication = Find<RadioButton>(window, "SegApplication");
            var segPreview = Find<RadioButton>(window, "SegPreview");

            Assert.True(segItems.IsChecked);
            Assert.Equal(Visibility.Visible, items.Visibility);
            Assert.Equal(Visibility.Collapsed, application.Visibility);
            Assert.Equal(Visibility.Collapsed, preview.Visibility);

            segApplication.IsChecked = true;
            Assert.Equal(Visibility.Collapsed, items.Visibility);
            Assert.Equal(Visibility.Visible, application.Visibility);
            Assert.Equal(Visibility.Collapsed, preview.Visibility);

            segPreview.IsChecked = true;
            Assert.Equal(Visibility.Collapsed, items.Visibility);
            Assert.Equal(Visibility.Collapsed, application.Visibility);
            Assert.Equal(Visibility.Visible, preview.Visibility);

            // Criar um menu a partir de outra seção abre os itens: é o primeiro passo de quem cria.
            Click(Find<Button>(window, "BtnNewMenuProfile"));
            Assert.True(segItems.IsChecked);
            Assert.Equal(Visibility.Visible, items.Visibility);

            CloseDiscarding(window);
        });
    }

    [Fact]
    public void Cabecalho_ResumeAsCondicoesDoMenuSelecionado()
    {
        RunSta(() =>
        {
            var settings = AppSettings.CreateDefault();
            var profile = new CustomMenuProfile
            {
                Name = "Downloads",
                ProcessName = "explorer",
                TitleContains = "Downloads",
                WindowClass = "CabinetWClass",
                Hotkey = "Ctrl+Alt+1",
                Contexts = { RadialContextKind.FileOrFolder },
            };
            profile.Extensions.AddRange(["pdf", "docx"]);
            profile.Items.Add(new CustomMenuItem
            {
                Label = "Renomear", Kind = CustomMenuItemKind.Action, Action = RadialAction.Rename,
            });
            settings.CustomProfiles.Add(profile);
            settings.CustomProfiles.Add(new CustomMenuProfile { Name = "Sem nada" });
            SettingsService.OverrideCurrentForTests(settings);

            SettingsWindow window = OpenSettings();
            var summary = Find<TextBlock>(window, "ProfileSummary");

            Assert.Contains("explorer", summary.Text);
            Assert.Contains("pdf, docx", summary.Text);
            Assert.Contains(Loc.Format("Menus.SummaryTitleContains", "Downloads"), summary.Text);
            Assert.Contains("CabinetWClass", summary.Text);
            Assert.Contains(Loc.Get("Menus.SummaryItemsOne"), summary.Text);
            Assert.Contains("Ctrl+Alt+1", summary.Text);

            // Editar em Aplicação atualiza o resumo na hora, mesmo com a seção oculta.
            Find<TextBox>(window, "TxtProfileProcess").Text = "";
            Assert.StartsWith(Loc.Get("Menus.AnyApp"), summary.Text);

            // Um menu sem condições nem itens resume exatamente isso.
            Find<ListBox>(window, "ListMenuProfiles").SelectedIndex = 1;
            Assert.Equal(
                $"{Loc.Get("Menus.AnyApp")}  ·  {Loc.Format("Menus.SummaryItemsMany", 0)}",
                summary.Text);

            CloseDiscarding(window);
        });
    }

    [Fact]
    public void EstadoVazio_AdicionarPrimeiroItem_RevelaListaEEditor()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            Click(Find<Button>(window, "BtnAddMenuProfile"));

            Assert.Equal(Visibility.Visible, Find<Border>(window, "PnlNoItems").Visibility);
            Assert.Equal(Visibility.Collapsed, Find<Grid>(window, "ItemsWorkspace").Visibility);

            Click(Find<Button>(window, "BtnAddFirstItem"));

            Assert.Equal(Visibility.Collapsed, Find<Border>(window, "PnlNoItems").Visibility);
            Assert.Equal(Visibility.Visible, Find<Grid>(window, "ItemsWorkspace").Visibility);
            Assert.Equal(Visibility.Visible, Find<StackPanel>(window, "PnlItemEditor").Visibility);

            var profile = (CustomMenuProfile)Find<ListBox>(window, "ListMenuProfiles").Items[0];
            Assert.Single(profile.Items);

            // Remover o único item devolve o convite em vez de uma lista vazia.
            Click(Find<Button>(window, "BtnRemoveMenuItem"));
            Assert.Equal(Visibility.Visible, Find<Border>(window, "PnlNoItems").Visibility);
            Assert.Equal(Visibility.Collapsed, Find<Grid>(window, "ItemsWorkspace").Visibility);

            CloseDiscarding(window);
        });
    }

    [Fact]
    public void Contextos_ChipTodos_RepresentaNenhumContextoSalvo()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            Click(Find<Button>(window, "BtnAddMenuProfile"));

            ToggleButton[] chips = Find<WrapPanel>(window, "PnlContexts").Children.OfType<ToggleButton>().ToArray();
            ToggleButton all = chips.Single(c => c.Tag is null);
            ToggleButton file = ContextChip(window, RadialContextKind.FileOrFolder);
            var profile = (CustomMenuProfile)Find<ListBox>(window, "ListMenuProfiles").Items[0];

            Assert.True(all.IsChecked);
            Assert.Empty(profile.Contexts);

            // Um contexto específico assume o lugar de "Todos".
            file.IsChecked = true;
            Assert.False(all.IsChecked);
            Assert.Equal(new[] { RadialContextKind.FileOrFolder }, profile.Contexts);

            // Desmarcar o último contexto volta para "Todos".
            file.IsChecked = false;
            Assert.True(all.IsChecked);
            Assert.Empty(profile.Contexts);

            // Marcar "Todos" limpa qualquer contexto específico.
            file.IsChecked = true;
            all.IsChecked = true;
            Assert.False(file.IsChecked);
            Assert.Empty(profile.Contexts);

            // "Todos" não pode ser desmarcado sozinho.
            all.IsChecked = false;
            Assert.True(all.IsChecked);
            Assert.Empty(profile.Contexts);

            CloseDiscarding(window);
        });
    }

    [Fact]
    public void Contextos_AoTrocarDePerfil_OsChipsRefletemORascunho()
    {
        RunSta(() =>
        {
            var settings = AppSettings.CreateDefault();
            settings.CustomProfiles.Add(new CustomMenuProfile
            {
                Name = "Com contextos",
                Contexts = { RadialContextKind.FileOrFolder, RadialContextKind.TextField },
            });
            settings.CustomProfiles.Add(new CustomMenuProfile { Name = "Sem contexto" });
            SettingsService.OverrideCurrentForTests(settings);

            SettingsWindow window = OpenSettings();
            var profiles = Find<ListBox>(window, "ListMenuProfiles");
            ToggleButton all = Find<WrapPanel>(window, "PnlContexts").Children.OfType<ToggleButton>()
                .Single(c => c.Tag is null);

            // Primeiro perfil: só os contextos salvos ficam marcados.
            Assert.False(all.IsChecked);
            Assert.True(ContextChip(window, RadialContextKind.FileOrFolder).IsChecked);
            Assert.True(ContextChip(window, RadialContextKind.TextField).IsChecked);
            Assert.False(ContextChip(window, RadialContextKind.TitleBar).IsChecked);

            // Segundo perfil sem contextos: "Todos" assume e nada específico fica marcado.
            profiles.SelectedIndex = 1;
            Assert.True(all.IsChecked);
            Assert.DoesNotContain(
                Find<WrapPanel>(window, "PnlContexts").Children.OfType<ToggleButton>().Where(c => c != all),
                c => c.IsChecked == true);

            // Voltar ao primeiro restaura a seleção sem alterar o rascunho.
            profiles.SelectedIndex = 0;
            var first = (CustomMenuProfile)profiles.Items[0];
            Assert.False(all.IsChecked);
            Assert.True(ContextChip(window, RadialContextKind.FileOrFolder).IsChecked);
            Assert.True(ContextChip(window, RadialContextKind.TextField).IsChecked);
            Assert.Equal(
                new[] { RadialContextKind.FileOrFolder, RadialContextKind.TextField },
                first.Contexts.ToArray());

            window.Close();
        });
    }

    // ------------------------------------------------------------------
    // Prévia
    // ------------------------------------------------------------------

    [Fact]
    public void Item_EdicaoAtualizaPrevia_ExtraESubstituicao()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            Click(Find<Button>(window, "BtnAddMenuProfile"));
            ContextChip(window, RadialContextKind.FileOrFolder).IsChecked = true;
            Click(Find<Button>(window, "BtnAddMenuItem"));   // ação padrão: Converter (fatia com submenu)

            MenuPreview preview = PreviewOf(window);
            Assert.Contains("arquivos e pastas", Find<TextBlock>(window, "PreviewContext").Text);
            Assert.Single(preview.Current, e => e.Status == CustomMergeStatus.Extra);
            Assert.DoesNotContain(preview.Current, e => e.Status == CustomMergeStatus.Replacement);

            SelectTag(Find<ComboBox>(window, "CmbItemAction"), RadialAction.CopyPath);
            CustomMergeEntry replacement = Assert.Single(preview.Current, e => e.Status == CustomMergeStatus.Replacement);
            Assert.Equal("Novo item", replacement.Slice.Label);
            Assert.DoesNotContain(preview.Current, e => e.Status == CustomMergeStatus.Extra);
            Assert.Equal(6, preview.Current.Count);   // 6 fatias automáticas de arquivo/pasta na prévia

            Find<CheckBox>(window, "ChkItemEnabled").IsChecked = false;
            Assert.DoesNotContain(preview.Current,
                e => e.Status is CustomMergeStatus.Extra or CustomMergeStatus.Replacement);
            Assert.Equal(6, preview.Current.Count);

            CloseDiscarding(window);
        });
    }

    [Fact]
    public void Previa_EdicaoDeTexto_AtualizaSoDepoisDoDebounce()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            Click(Find<Button>(window, "BtnAddMenuProfile"));
            ContextChip(window, RadialContextKind.FileOrFolder).IsChecked = true;
            Click(Find<Button>(window, "BtnAddMenuItem"));

            MenuPreview preview = PreviewOf(window);
            Assert.Contains(preview.Current, e => e.Slice.Label == "Novo item");

            var label = Find<TextBox>(window, "TxtItemLabel");
            string final = new('A', 30);
            for (int i = 1; i <= final.Length; i++) label.Text = final[..i];

            // Sem bombear o Dispatcher, o debounce ainda não rodou.
            Assert.Contains(preview.Current, e => e.Slice.Label == "Novo item");
            Assert.DoesNotContain(preview.Current, e => e.Slice.Label == final);

            Pump(300);

            Assert.Contains(preview.Current, e => e.Slice.Label == final);
            Assert.DoesNotContain(preview.Current, e => e.Slice.Label == "Novo item");

            CloseDiscarding(window);
        });
    }

    [Fact]
    public void Fechar_ComEdicaoPendente_AplicaUltimaEdicaoENaoDeixaTimer()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            Click(Find<Button>(window, "BtnAddMenuProfile"));
            ContextChip(window, RadialContextKind.FileOrFolder).IsChecked = true;
            Click(Find<Button>(window, "BtnAddMenuItem"));

            Find<TextBox>(window, "TxtItemLabel").Text = "Fechando já";
            Assert.True(PreviewTimerOf(window)?.IsEnabled);

            // O debounce está pendente quando a janela fecha; o aviso de descarte ainda aparece.
            CloseDiscarding(window);

            Assert.True(PreviewTimerOf(window) is null or { IsEnabled: false });
            Assert.Contains(PreviewOf(window).Current, e => e.Slice.Label == "Fechando já");
        });
    }

    [Fact]
    public void Previa_ComZeroOitoENoveItens_RespeitaOLimite()
    {
        RunSta(() =>
        {
            // Sem perfis: prévia vazia.
            SettingsWindow empty = OpenSettings();
            Assert.Empty(PreviewOf(empty).Current);
            Assert.Contains("Nenhum menu selecionado", Find<TextBlock>(empty, "PreviewContext").Text);
            empty.Close();

            // 8 itens: 7 substituem as fatias da barra de título + 1 extra = 8 fatias, nada descartado.
            var eight = AppSettings.CreateDefault();
            eight.CustomProfiles.Add(TitleBarProfile(8));
            SettingsService.OverrideCurrentForTests(eight);
            SettingsWindow w8 = OpenSettings();
            MenuPreview preview8 = PreviewOf(w8);
            Assert.Equal(8, preview8.Current.Count);
            Assert.Equal(7, preview8.Current.Count(e => e.Status == CustomMergeStatus.Replacement));
            Assert.Equal(1, preview8.Current.Count(e => e.Status == CustomMergeStatus.Extra));
            Assert.Equal(Visibility.Collapsed, Find<TextBlock>(w8, "PreviewWarning").Visibility);
            w8.Close();

            // 9 itens (JSON editado à mão): o extra que não cabe é avisado e fica de fora.
            var nine = AppSettings.CreateDefault();
            nine.CustomProfiles.Add(TitleBarProfile(9));
            SettingsService.OverrideCurrentForTests(nine);
            SettingsWindow w9 = OpenSettings();
            MenuPreview preview9 = PreviewOf(w9);
            Assert.Equal(8, preview9.Current.Count);
            Assert.Equal(7, preview9.Current.Count(e => e.Status == CustomMergeStatus.Replacement));
            Assert.Equal(1, preview9.Current.Count(e => e.Status == CustomMergeStatus.Extra));
            TextBlock warning = Find<TextBlock>(w9, "PreviewWarning");
            Assert.Equal(Visibility.Visible, warning.Visibility);
            Assert.Contains("1 item será ignorado", warning.Text);
            w9.Close();
        });
    }

    // ------------------------------------------------------------------
    // Submenus
    // ------------------------------------------------------------------

    private static CustomMenuItem SubmenuItem(int optionCount = 2)
    {
        var item = new CustomMenuItem { Label = "Ações", Kind = CustomMenuItemKind.Submenu };
        for (int i = 0; i < optionCount; i++)
        {
            item.Options.Add(new CustomMenuItem
            {
                Label = $"Opção {i + 1}",
                Kind = CustomMenuItemKind.Action,
                Action = RadialAction.CopyPath,
            });
        }

        return item;
    }

    private static AppSettings SettingsWithSubmenu(CustomMenuItem item)
    {
        var settings = AppSettings.CreateDefault();
        settings.CustomProfiles.Add(new CustomMenuProfile
        {
            Name = "Menu",
            Contexts = { RadialContextKind.FileOrFolder },
            Items = { item },
        });
        return settings;
    }

    private static void ShowMessageContaining(string text, Action<string> capture)
    {
        OnShown(w =>
        {
            string? shown = Descendants(w).OfType<TextBlock>()
                .FirstOrDefault(t => t.Visibility == Visibility.Visible && t.Text.Contains(text))?.Text;
            if (shown is not null) capture(shown);
            Click(ButtonsOf(w).Single(b => (string)b.Content == "OK"));
        });
    }

    [Fact]
    public void Submenu_EditorResumePainelBadgeEPrevia()
    {
        RunSta(() =>
        {
            SettingsService.OverrideCurrentForTests(SettingsWithSubmenu(SubmenuItem()));
            SettingsWindow window = OpenSettings();
            SelectTab(window, "Menus");
            Pump(300);
            Find<ListBox>(window, "ListItems").SelectedIndex = 0;

            Assert.Equal(Visibility.Visible, Find<StackPanel>(window, "PnlItemSubmenu").Visibility);
            Assert.Equal(Visibility.Collapsed, Find<Border>(window, "BdItemTarget").Visibility);
            Assert.Equal("2 de 8 opções", Find<TextBlock>(window, "TxtSubmenuCount").Text);
            Assert.Contains("2 ativas", Find<TextBlock>(window, "TxtSubmenuPreview").Text);
            Assert.True(Find<Button>(window, "BtnEditSubmenuOptions").IsEnabled);

            Pump(200);
            TextBlock? badge = Descendants(window).OfType<TextBlock>()
                .FirstOrDefault(t => t.Visibility == Visibility.Visible && t.Text == "2 opções");
            Assert.NotNull(badge);

            CustomMergeEntry entry = Assert.Single(PreviewOf(window).Current, e => e.Slice.Label == "Ações");
            Assert.Equal(2, entry.Slice.SubSlices!.Count);

            window.Close();
        });
    }

    [Fact]
    public void Submenu_InvalidoNaPrevia_AvisaENaoAparece()
    {
        RunSta(() =>
        {
            SettingsService.OverrideCurrentForTests(SettingsWithSubmenu(SubmenuItem(1)));
            SettingsWindow window = OpenSettings();
            Find<ListBox>(window, "ListItems").SelectedIndex = 0;

            Assert.DoesNotContain(PreviewOf(window).Current, e => e.Slice.Label == "Ações");
            TextBlock warning = Find<TextBlock>(window, "PreviewWarning");
            Assert.Equal(Visibility.Visible, warning.Visibility);
            Assert.Contains("1 submenu precisa de 2 opções ativas", warning.Text);

            window.Close();
        });
    }

    [Fact]
    public void Submenu_CriarPeloEditor_ExigeDuasOpcoesESalva()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            Click(Find<Button>(window, "BtnAddMenuProfile"));
            Click(Find<Button>(window, "BtnAddMenuItem"));
            SelectTag(Find<ComboBox>(window, "CmbItemKind"), CustomMenuItemKind.Submenu);

            Assert.Equal(Visibility.Visible, Find<StackPanel>(window, "PnlItemSubmenu").Visibility);
            Assert.Equal(Visibility.Collapsed, Find<Border>(window, "BdItemTarget").Visibility);

            string? blocked = null;
            ShowMessageContaining("2 opções ativas", text => blocked = text);
            Click(Find<Button>(window, "BtnSave"));

            Assert.NotNull(blocked);
            Assert.Null(_saved);
            Assert.True(window.IsVisible);

            SubmenuEditorWindow.BeforeShowForTests = dialog =>
            {
                Click(Find<Button>(dialog, "BtnAddOption"));
                Find<TextBox>(dialog, "TxtOptionLabel").Text = "Copiar caminho";
                Click(Find<Button>(dialog, "BtnAddOption"));
                Find<TextBox>(dialog, "TxtOptionLabel").Text = "Fechar";
                Click(Find<Button>(dialog, "BtnDone"));
            };
            Click(Find<Button>(window, "BtnEditSubmenuOptions"));

            Assert.Contains("2 ativas", Find<TextBlock>(window, "TxtSubmenuPreview").Text);

            Click(Find<Button>(window, "BtnSave"));

            Assert.NotNull(_saved);
            CustomMenuItem saved = Assert.Single(Assert.Single(_saved!.CustomProfiles).Items);
            Assert.Equal(CustomMenuItemKind.Submenu, saved.Kind);
            Assert.Equal(["Copiar caminho", "Fechar"], saved.Options.Select(o => o.Label).ToArray());
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public void Submenu_Dialogo_ValidaAlvoDaOpcao()
    {
        RunSta(() =>
        {
            SettingsService.OverrideCurrentForTests(SettingsWithSubmenu(SubmenuItem()));
            SettingsWindow window = OpenSettings();
            Find<ListBox>(window, "ListItems").SelectedIndex = 0;

            string? shown = null;
            SubmenuEditorWindow.BeforeShowForTests = dialog =>
            {
                Find<ListBox>(dialog, "ListOptions").SelectedIndex = 1;
                SelectTag(Find<ComboBox>(dialog, "CmbOptionKind"), CustomMenuItemKind.OpenUrl);
                Find<TextBox>(dialog, "TxtOptionUrl").Text = "file:///C:/Windows/System32/calc.exe";

                ShowMessageContaining("http://", text => shown = text);
                Click(Find<Button>(dialog, "BtnDone"));
                if (dialog.IsVisible) dialog.Close();
            };

            Click(Find<Button>(window, "BtnEditSubmenuOptions"));

            Assert.NotNull(shown);
            Assert.True(window.IsVisible);
            window.Close();
        });
    }

    [Fact]
    public void Submenu_Dialogo_SalvaOpcaoComTransformacao()
    {
        RunSta(() =>
        {
            SettingsService.OverrideCurrentForTests(SettingsWithSubmenu(SubmenuItem()));
            SettingsWindow window = OpenSettings();
            Find<ListBox>(window, "ListItems").SelectedIndex = 0;

            SubmenuEditorWindow.BeforeShowForTests = dialog =>
            {
                Find<ListBox>(dialog, "ListOptions").SelectedIndex = 0;
                SelectTag(Find<ComboBox>(dialog, "CmbOptionKind"), CustomMenuItemKind.TextTransform);
                Assert.Equal(Visibility.Visible, Find<StackPanel>(dialog, "PnlOptionTextTransform").Visibility);
                SelectTag(Find<ComboBox>(dialog, "CmbOptionTransform"), CustomTextTransform.SentenceCase);
                Click(Find<Button>(dialog, "BtnDone"));
            };

            Click(Find<Button>(window, "BtnEditSubmenuOptions"));
            Click(Find<Button>(window, "BtnSave"));

            Assert.NotNull(_saved);
            CustomMenuItem saved = Assert.Single(Assert.Single(_saved!.CustomProfiles).Items);
            Assert.Equal(CustomMenuItemKind.TextTransform, saved.Options[0].Kind);
            Assert.Equal(CustomTextTransform.SentenceCase, saved.Options[0].Transform);
            Assert.Equal(CustomTextTransform.Upper, saved.Options[1].Transform);
        });
    }

    [Fact]
    public void Submenu_Dialogo_NaoOfereceSubmenuNemRotina()
    {
        RunSta(() =>
        {
            SettingsService.OverrideCurrentForTests(SettingsWithSubmenu(SubmenuItem()));
            SettingsWindow window = OpenSettings();
            Find<ListBox>(window, "ListItems").SelectedIndex = 0;

            SubmenuEditorWindow.BeforeShowForTests = dialog =>
            {
                CustomMenuItemKind[] offered = Find<ComboBox>(dialog, "CmbOptionKind").Items
                    .Cast<ComboBoxItem>()
                    .Select(i => (CustomMenuItemKind)i.Tag)
                    .ToArray();
                Assert.DoesNotContain(CustomMenuItemKind.Submenu, offered);
                Assert.DoesNotContain(CustomMenuItemKind.Routine, offered);
                dialog.Close();
            };

            Click(Find<Button>(window, "BtnEditSubmenuOptions"));
            window.Close();
        });
    }

    // ------------------------------------------------------------------
    // Rotinas
    // ------------------------------------------------------------------

    private static CustomRoutineStep RoutineAction(RadialAction action) => new()
    {
        Kind = CustomRoutineStepKind.Action,
        Action = action,
    };

    private static CustomRoutineStep RoutineDelay(int milliseconds) => new()
    {
        Kind = CustomRoutineStepKind.Delay,
        DelayMilliseconds = milliseconds,
    };

    private static CustomMenuItem RoutineItem(params CustomRoutineStep[] steps) => new()
    {
        Label = "Copiar e colar",
        Kind = CustomMenuItemKind.Routine,
        Steps = steps.ToList(),
    };

    private static AppSettings SettingsWithRoutine(CustomMenuItem item)
    {
        var settings = AppSettings.CreateDefault();
        settings.CustomProfiles.Add(new CustomMenuProfile
        {
            Name = "Menu",
            Contexts = { RadialContextKind.FileOrFolder },
            Items = { item },
        });
        return settings;
    }

    [Fact]
    public void Rotina_PainelResumePassosEPrevia()
    {
        RunSta(() =>
        {
            SettingsService.OverrideCurrentForTests(SettingsWithRoutine(
                RoutineItem(RoutineAction(RadialAction.CopyPath), RoutineDelay(250), RoutineAction(RadialAction.Paste))));
            SettingsWindow window = OpenSettings();
            SelectTab(window, "Menus");
            Pump(300);
            Find<ListBox>(window, "ListItems").SelectedIndex = 0;

            Assert.Equal(Visibility.Visible, Find<StackPanel>(window, "PnlItemRoutine").Visibility);
            Assert.Equal(Visibility.Collapsed, Find<Border>(window, "BdItemTarget").Visibility);
            Assert.Equal("3 de 8 passos", Find<TextBlock>(window, "TxtRoutineCount").Text);
            Assert.Equal("Copiar caminho → 250 ms → Colar", Find<TextBlock>(window, "TxtRoutinePreview").Text);
            Assert.Equal(Visibility.Collapsed, Find<TextBlock>(window, "TxtRoutineWarning").Visibility);
            Assert.True(Find<Button>(window, "BtnEditRoutineSteps").IsEnabled);

            CustomMergeEntry entry = Assert.Single(PreviewOf(window).Current, e => e.Slice.Label == "Copiar e colar");
            Assert.Null(entry.Slice.SubSlices);

            window.Close();
        });
    }

    [Fact]
    public void Rotina_InvalidaNoPainelEAvisoDaPrevia()
    {
        RunSta(() =>
        {
            SettingsService.OverrideCurrentForTests(SettingsWithRoutine(
                RoutineItem(RoutineAction(RadialAction.Convert))));
            SettingsWindow window = OpenSettings();
            Find<ListBox>(window, "ListItems").SelectedIndex = 0;

            TextBlock warning = Find<TextBlock>(window, "TxtRoutineWarning");
            Assert.Equal(Visibility.Visible, warning.Visibility);
            Assert.Contains("não é permitida", warning.Text);

            Assert.DoesNotContain(PreviewOf(window).Current, e => e.Slice.Label == "Copiar e colar");
            TextBlock previewWarning = Find<TextBlock>(window, "PreviewWarning");
            Assert.Equal(Visibility.Visible, previewWarning.Visibility);
            Assert.Contains("1 rotina inválida não aparece", previewWarning.Text);

            window.Close();
        });
    }

    [Fact]
    public void Rotina_CriarPeloEditor_ExigeAcaoESalva()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            Click(Find<Button>(window, "BtnAddMenuProfile"));
            Click(Find<Button>(window, "BtnAddMenuItem"));
            SelectTag(Find<ComboBox>(window, "CmbItemKind"), CustomMenuItemKind.Routine);

            Assert.Equal(Visibility.Visible, Find<StackPanel>(window, "PnlItemRoutine").Visibility);
            Assert.Equal(Visibility.Collapsed, Find<Border>(window, "BdItemTarget").Visibility);
            Assert.Equal("0 de 8 passos", Find<TextBlock>(window, "TxtRoutineCount").Text);

            string? blocked = null;
            ShowMessageContaining("não tem passos", text => blocked = text);
            Click(Find<Button>(window, "BtnSave"));

            Assert.NotNull(blocked);
            Assert.Null(_saved);
            Assert.True(window.IsVisible);

            RoutineEditorWindow.BeforeShowForTests = dialog =>
            {
                Click(Find<Button>(dialog, "BtnAddAction"));
                Click(Find<Button>(dialog, "BtnAddDelay"));
                Click(Find<Button>(dialog, "BtnDone"));
            };
            Click(Find<Button>(window, "BtnEditRoutineSteps"));

            Assert.Equal("2 de 8 passos", Find<TextBlock>(window, "TxtRoutineCount").Text);
            Assert.Equal("Copiar caminho → 250 ms", Find<TextBlock>(window, "TxtRoutinePreview").Text);

            Click(Find<Button>(window, "BtnSave"));

            Assert.NotNull(_saved);
            CustomMenuItem saved = Assert.Single(Assert.Single(_saved!.CustomProfiles).Items);
            Assert.Equal(CustomMenuItemKind.Routine, saved.Kind);
            Assert.Equal(CustomMenuItem.RoutineSchemaVersion, saved.RoutineVersion);
            Assert.Equal(
                [CustomRoutineStepKind.Action, CustomRoutineStepKind.Delay],
                saved.Steps.Select(s => s.Kind).ToArray());
            Assert.Equal(RadialAction.CopyPath, saved.Steps[0].Action);
            Assert.Equal(250, saved.Steps[1].DelayMilliseconds);
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public void Rotina_Dialogo_SoOfereceAcoesPermitidasEExigePasso()
    {
        RunSta(() =>
        {
            SettingsService.OverrideCurrentForTests(SettingsWithRoutine(RoutineItem()));
            SettingsWindow window = OpenSettings();
            Find<ListBox>(window, "ListItems").SelectedIndex = 0;

            RoutineEditorWindow.BeforeShowForTests = dialog =>
            {
                RadialAction[] offered = Find<ComboBox>(dialog, "CmbStepAction").Items
                    .Cast<ComboBoxItem>()
                    .Select(i => (RadialAction)i.Tag)
                    .ToArray();
                Assert.Equal(CustomMenuChoices.RoutineActions.Select(a => a.Action).ToArray(), offered);
                Assert.DoesNotContain(RadialAction.Convert, offered);
                Assert.DoesNotContain(RadialAction.Close, offered);

                string? shown = null;
                ShowMessageContaining("Adicione pelo menos uma ação", text => shown = text);
                Click(Find<Button>(dialog, "BtnDone"));

                Assert.NotNull(shown);
                Assert.True(dialog.IsVisible);

                Click(Find<Button>(dialog, "BtnAddAction"));
                Click(Find<Button>(dialog, "BtnDone"));
            };

            Click(Find<Button>(window, "BtnEditRoutineSteps"));

            Assert.Equal("1 de 8 passos", Find<TextBlock>(window, "TxtRoutineCount").Text);
            CloseDiscarding(window);
        });
    }

    [Fact]
    public void Rotina_ComOitoPassos_NaoDeixaAdicionarMais()
    {
        RunSta(() =>
        {
            CustomRoutineStep[] steps = Enumerable.Range(0, 8)
                .Select(_ => RoutineAction(RadialAction.CopyPath))
                .ToArray();
            SettingsService.OverrideCurrentForTests(SettingsWithRoutine(RoutineItem(steps)));
            SettingsWindow window = OpenSettings();
            Find<ListBox>(window, "ListItems").SelectedIndex = 0;

            RoutineEditorWindow.BeforeShowForTests = dialog =>
            {
                Assert.False(Find<Button>(dialog, "BtnAddAction").IsEnabled);
                Assert.False(Find<Button>(dialog, "BtnAddDelay").IsEnabled);
                Assert.Equal("8 de 8 passos · 0 ms de pausa", Find<TextBlock>(dialog, "TxtStepCount").Text);
                dialog.Close();
            };

            Click(Find<Button>(window, "BtnEditRoutineSteps"));
            window.Close();
        });
    }

    [Fact]
    public void Rotina_AcaoQueMudaFocoNoMeio_AvisaENaoSalva()
    {
        RunSta(() =>
        {
            SettingsService.OverrideCurrentForTests(SettingsWithRoutine(
                RoutineItem(RoutineAction(RadialAction.SearchWeb), RoutineAction(RadialAction.CopyPath))));
            SettingsWindow window = OpenSettings();
            Find<ListBox>(window, "ListItems").SelectedIndex = 0;

            TextBlock warning = Find<TextBlock>(window, "TxtRoutineWarning");
            Assert.Equal(Visibility.Visible, warning.Visibility);
            Assert.Contains("só pode ser o último passo", warning.Text);

            RoutineEditorWindow.BeforeShowForTests = dialog =>
            {
                string? shown = null;
                ShowMessageContaining("só pode ser o último passo", text => shown = text);
                Click(Find<Button>(dialog, "BtnDone"));
                Assert.NotNull(shown);
                Assert.True(dialog.IsVisible);
                dialog.Close();
            };
            Click(Find<Button>(window, "BtnEditRoutineSteps"));

            string? blocked = null;
            ShowMessageContaining("só pode ser o último passo", text => blocked = text);
            Click(Find<Button>(window, "BtnSave"));

            Assert.NotNull(blocked);
            Assert.Null(_saved);
            Assert.True(window.IsVisible);
            window.Close();
        });
    }

    // ------------------------------------------------------------------
    // Importar e exportar
    // ------------------------------------------------------------------

    private static CustomMenuProfile TransferProfile() => new()
    {
        Name = "Importado",
        Contexts = { RadialContextKind.Generic },
        Items =
        {
            new CustomMenuItem
            {
                Label = "Copiar caminho",
                Kind = CustomMenuItemKind.Action,
                Action = RadialAction.CopyPath,
            },
        },
    };

    private string ExportToFile(CustomMenuProfile profile)
    {
        string path = _temp.Combine("menus.json");
        Assert.True(CustomMenuTransfer.TryExportToFile(path, [profile], out string? reason), reason);
        return path;
    }

    [Fact]
    public void Importar_ArquivoValido_EntraNoRascunhoComIdsNovosENaoSalva()
    {
        RunSta(() =>
        {
            CustomMenuProfile source = TransferProfile();
            SettingsWindow.ChooseImportPathForTests = () => ExportToFile(source);

            SettingsWindow window = OpenSettings();
            var profiles = Find<ListBox>(window, "ListMenuProfiles");
            Assert.Empty(profiles.Items);
            Assert.False(Find<Button>(window, "BtnExportMenus").IsEnabled);

            string? shown = null;
            ShowMessageContaining("foram adicionados ao rascunho", text => shown = text);
            Click(Find<Button>(window, "BtnImportMenusEmpty"));

            Assert.NotNull(shown);
            Assert.Contains("as mesmas regras de segurança", shown);
            CustomMenuProfile imported = Assert.Single(profiles.Items.Cast<CustomMenuProfile>());
            Assert.Equal("Importado", imported.Name);
            Assert.NotEqual(source.Id, imported.Id);
            Assert.Equal("Importado", Find<TextBox>(window, "TxtProfileName").Text);
            Assert.True(Find<Button>(window, "BtnExportMenus").IsEnabled);
            Assert.Null(_saved);
            Assert.Empty(SettingsService.Current.CustomProfiles);

            CloseDiscarding(window);
            Assert.Null(_saved);
        });
    }

    [Fact]
    public void Importar_AlvoInvalido_RecusaArquivoInteiro()
    {
        RunSta(() =>
        {
            CustomMenuProfile source = TransferProfile();
            source.Name = "Site";
            source.Items[0] = new CustomMenuItem
            {
                Label = "Site",
                Kind = CustomMenuItemKind.OpenUrl,
                Target = "file:///C:/Windows/System32/calc.exe",
            };
            SettingsWindow.ChooseImportPathForTests = () => ExportToFile(source);

            SettingsWindow window = OpenSettings();

            string? shown = null;
            ShowMessageContaining("Nenhum menu foi adicionado", text => shown = text);
            Click(Find<Button>(window, "BtnImportMenusEmpty"));

            Assert.NotNull(shown);
            Assert.Contains("http://", shown);
            Assert.Empty(Find<ListBox>(window, "ListMenuProfiles").Items);
            Assert.True(window.IsVisible);
            Assert.Null(_saved);

            window.Close();
        });
    }

    [Fact]
    public void Importar_Cancelado_NaoAlteraRascunho()
    {
        RunSta(() =>
        {
            SettingsWindow.ChooseImportPathForTests = () => null;
            SettingsWindow window = OpenSettings();

            Click(Find<Button>(window, "BtnImportMenusEmpty"));

            Assert.Empty(Find<ListBox>(window, "ListMenuProfiles").Items);
            Assert.True(window.IsVisible);
            Assert.Null(_saved);
            window.Close();
        });
    }

    [Fact]
    public void Exportar_RascunhoAtual_GeraArquivoLegivelSemSalvarConfiguracoes()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            Click(Find<Button>(window, "BtnAddMenuProfile"));
            Find<TextBox>(window, "TxtProfileName").Text = "Meu menu";
            Click(Find<Button>(window, "BtnAddMenuItem"));
            Find<TextBox>(window, "TxtItemLabel").Text = "Copiar caminho";
            SelectTag(Find<ComboBox>(window, "CmbItemAction"), RadialAction.CopyPath);

            string path = _temp.Combine("exportado.json");
            SettingsWindow.ChooseExportPathForTests = () => path;

            string? shown = null;
            ShowMessageContaining("foram exportados para", text => shown = text);
            Click(Find<Button>(window, "BtnExportMenus"));

            Assert.NotNull(shown);
            Assert.Contains(path, shown);
            Assert.True(File.Exists(path));
            Assert.Null(_saved);
            Assert.Empty(SettingsService.Current.CustomProfiles);

            Assert.True(CustomMenuTransfer.TryImportFromFile(
                path, Array.Empty<string?>(), 0, out List<CustomMenuProfile> imported, out string? reason), reason);
            CustomMenuProfile exported = Assert.Single(imported);
            Assert.Equal("Meu menu", exported.Name);
            CustomMenuItem item = Assert.Single(exported.Items);
            Assert.Equal("Copiar caminho", item.Label);
            Assert.Equal(RadialAction.CopyPath, item.Action);

            CloseDiscarding(window);
        });
    }

    // ------------------------------------------------------------------
    // Salvar e cancelar
    // ------------------------------------------------------------------

    [Fact]
    public void Salvar_PersisteCopiaProfunda()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            Click(Find<Button>(window, "BtnAddMenuProfile"));
            Find<TextBox>(window, "TxtProfileName").Text = "Meu menu";
            Click(Find<Button>(window, "BtnAddMenuItem"));
            Find<TextBox>(window, "TxtItemLabel").Text = "Bloco de notas";
            SelectTag(Find<ComboBox>(window, "CmbItemKind"), CustomMenuItemKind.Launch);
            Find<TextBox>(window, "TxtItemLaunch").Text = "notepad.exe";

            Click(Find<Button>(window, "BtnSave"));

            Assert.NotNull(_saved);
            CustomMenuProfile saved = Assert.Single(_saved!.CustomProfiles);
            Assert.Equal("Meu menu", saved.Name);
            CustomMenuItem savedItem = Assert.Single(saved.Items);
            Assert.Equal("Bloco de notas", savedItem.Label);
            Assert.Equal(CustomMenuItemKind.Launch, savedItem.Kind);
            Assert.Equal("notepad.exe", savedItem.Target);

            var draft = (CustomMenuProfile)Find<ListBox>(window, "ListMenuProfiles").Items[0];
            Assert.NotSame(draft, saved);
            Assert.NotSame(draft.Items[0], savedItem);
            Assert.False(window.IsVisible);
            Assert.True(PreviewTimerOf(window) is null or { IsEnabled: false });
        });
    }

    [Fact]
    public void Salvar_NormalizaExtensoesEClasseDaJanela()
    {
        RunSta(() =>
        {
            var original = AppSettings.CreateDefault();
            original.CustomProfiles =
            [
                new CustomMenuProfile
                {
                    Id = "p1",
                    Name = "PDFs",
                    Extensions = [".pdf", ".png"],
                    WindowClass = "CabinetWClass"
                }
            ];
            SettingsService.OverrideCurrentForTests(original);

            SettingsWindow window = OpenSettings();
            Assert.Equal(".pdf, .png", Find<TextBox>(window, "TxtProfileExtensions").Text);
            Assert.Equal("CabinetWClass", Find<TextBox>(window, "TxtProfileWindowClass").Text);

            Find<TextBox>(window, "TxtProfileExtensions").Text = "PDF, *.jpg, com espaço";
            Find<TextBox>(window, "TxtProfileWindowClass").Text = "  Chrome_WidgetWin_1  ";

            Click(Find<Button>(window, "BtnSave"));

            Assert.NotNull(_saved);
            CustomMenuProfile saved = Assert.Single(_saved!.CustomProfiles);
            Assert.Equal([".pdf", ".jpg"], saved.Extensions.ToArray());
            Assert.Equal("Chrome_WidgetWin_1", saved.WindowClass);
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public void Salvar_PersisteTransformacaoDeTexto()
    {
        RunSta(() =>
        {
            var original = AppSettings.CreateDefault();
            original.CustomProfiles =
            [
                new CustomMenuProfile
                {
                    Id = "p1",
                    Name = "Texto",
                    Items =
                    {
                        new CustomMenuItem
                        {
                            Id = "i1",
                            Label = "Maiúsculas",
                            Kind = CustomMenuItemKind.TextTransform,
                            Transform = CustomTextTransform.TitleCase,
                        }
                    }
                }
            ];
            SettingsService.OverrideCurrentForTests(original);

            SettingsWindow window = OpenSettings();
            Find<ListBox>(window, "ListItems").SelectedIndex = 0;

            Assert.Equal(Visibility.Visible, Find<StackPanel>(window, "PnlItemTextTransform").Visibility);
            Assert.Equal(
                CustomTextTransform.TitleCase,
                ((ComboBoxItem)Find<ComboBox>(window, "CmbItemTransform").SelectedItem).Tag);

            SelectTag(Find<ComboBox>(window, "CmbItemTransform"), CustomTextTransform.RemoveLineBreaks);
            Click(Find<Button>(window, "BtnSave"));

            Assert.NotNull(_saved);
            CustomMenuItem savedItem = Assert.Single(Assert.Single(_saved!.CustomProfiles).Items);
            Assert.Equal(CustomMenuItemKind.TextTransform, savedItem.Kind);
            Assert.Equal(CustomTextTransform.RemoveLineBreaks, savedItem.Transform);
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public void Salvar_PersisteAtalhoDoMenu()
    {
        RunSta(() =>
        {
            var original = AppSettings.CreateDefault();
            original.CustomProfiles =
            [
                new CustomMenuProfile
                {
                    Id = "p1",
                    Name = "Meus atalhos",
                    Hotkey = "Ctrl+Alt+2",
                    Items =
                    {
                        new CustomMenuItem
                        {
                            Id = "i1",
                            Label = "Bloco de notas",
                            Kind = CustomMenuItemKind.Launch,
                            Target = "notepad.exe",
                        }
                    }
                }
            ];
            SettingsService.OverrideCurrentForTests(original);

            SettingsWindow window = OpenSettings();
            Assert.Equal("Ctrl+Alt+2", Find<TextBox>(window, "TxtProfileHotkey").Text);

            Find<TextBox>(window, "TxtProfileHotkey").Text = " ctrl + alt + 1 ";
            Click(Find<Button>(window, "BtnSave"));

            Assert.NotNull(_saved);
            CustomMenuProfile saved = Assert.Single(_saved!.CustomProfiles);
            Assert.Equal("Ctrl+Alt+1", saved.Hotkey);
            Assert.False(window.IsVisible);
        });
    }

    private static void AddProfileWithLaunchItem(SettingsWindow window, string name)
    {
        Click(Find<Button>(window, "BtnAddMenuProfile"));
        Find<TextBox>(window, "TxtProfileName").Text = name;
        Click(Find<Button>(window, "BtnAddMenuItem"));
        Find<TextBox>(window, "TxtItemLabel").Text = "Bloco de notas";
        SelectTag(Find<ComboBox>(window, "CmbItemKind"), CustomMenuItemKind.Launch);
        Find<TextBox>(window, "TxtItemLaunch").Text = "notepad.exe";
    }

    [Fact]
    public void Salvar_AtalhoInvalido_MostraErroENaoPersiste()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            AddProfileWithLaunchItem(window, "Inválido");
            Find<TextBox>(window, "TxtProfileHotkey").Text = "Ctrl+Alt";

            Func<string?> message = CaptureDialog("não é válido");
            Click(Find<Button>(window, "BtnSave"));

            Assert.NotNull(message());
            Assert.Null(_saved);
            Assert.True(window.IsVisible);
            CloseDiscarding(window);
        });
    }

    [Fact]
    public void Salvar_AtalhoDaRodaPrincipal_MostraErroENaoPersiste()
    {
        RunSta(() =>
        {
            var original = AppSettings.CreateDefault();
            original.Hotkey = "Ctrl+Alt+Space";
            SettingsService.OverrideCurrentForTests(original);

            SettingsWindow window = OpenSettings();
            AddProfileWithLaunchItem(window, "Conflito");
            Find<TextBox>(window, "TxtProfileHotkey").Text = "ctrl+alt+space";

            Func<string?> message = CaptureDialog("roda principal");
            Click(Find<Button>(window, "BtnSave"));

            Assert.NotNull(message());
            Assert.Null(_saved);
            Assert.True(window.IsVisible);
            CloseDiscarding(window);
        });
    }

    [Fact]
    public void Salvar_AtalhoRepetido_MostraErroENaoPersiste()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            AddProfileWithLaunchItem(window, "Primeiro");
            Find<TextBox>(window, "TxtProfileHotkey").Text = "Ctrl+Alt+1";

            Click(Find<Button>(window, "BtnDuplicateMenuProfile"));

            // A cópia herda os itens, mas não o atalho: dois menus com o mesmo atalho não salvam.
            Assert.Equal("", Find<TextBox>(window, "TxtProfileHotkey").Text);
            Find<TextBox>(window, "TxtProfileHotkey").Text = "Ctrl+Alt+1";

            Func<string?> message = CaptureDialog("repete o atalho");
            Click(Find<Button>(window, "BtnSave"));

            Assert.NotNull(message());
            Assert.Null(_saved);
            Assert.True(window.IsVisible);
            CloseDiscarding(window);
        });
    }

    [Fact]
    public void Salvar_AtalhoSemItens_MostraErroENaoPersiste()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            Click(Find<Button>(window, "BtnAddMenuProfile"));
            Find<TextBox>(window, "TxtProfileName").Text = "Sem itens";
            Find<TextBox>(window, "TxtProfileHotkey").Text = "Ctrl+Alt+1";

            Func<string?> message = CaptureDialog("nenhum item ativo");
            Click(Find<Button>(window, "BtnSave"));

            Assert.NotNull(message());
            Assert.Null(_saved);
            Assert.True(window.IsVisible);
            CloseDiscarding(window);
        });
    }

    [Fact]
    public void Salvar_Invalido_MostraErroENaoPersiste()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            Click(Find<Button>(window, "BtnAddMenuProfile"));
            Click(Find<Button>(window, "BtnAddMenuItem"));
            Find<TextBox>(window, "TxtItemLabel").Text = "Site";
            SelectTag(Find<ComboBox>(window, "CmbItemKind"), CustomMenuItemKind.OpenUrl);
            Find<TextBox>(window, "TxtItemUrl").Text = "file:///C:/Windows/System32/calc.exe";

            string? shown = null;
            OnShown(w =>
            {
                shown = Descendants(w).OfType<TextBlock>()
                    .FirstOrDefault(t => t.Visibility == Visibility.Visible && t.Text.Contains("http://"))?.Text;
                Click(ButtonsOf(w).Single(b => (string)b.Content == "OK"));
            });

            Click(Find<Button>(window, "BtnSave"));

            Assert.NotNull(shown);
            Assert.Null(_saved);
            Assert.Empty(SettingsService.Current.CustomProfiles);
            Assert.True(window.IsVisible);

            CloseDiscarding(window);
        });
    }

    [Fact]
    public void Salvar_FalhaDeGravacao_MantemJanelaECurrent()
    {
        RunSta(() =>
        {
            AppSettings original = SettingsService.Current;
            SettingsService.SaveOverrideForTests = _ => throw new IOException("disco cheio");

            SettingsWindow window = OpenSettings();
            Find<CheckBox>(window, "ChkStartWithWindows").IsChecked = true;

            string? shown = null;
            OnShown(w =>
            {
                shown = Descendants(w).OfType<TextBlock>()
                    .FirstOrDefault(t => t.Visibility == Visibility.Visible && t.Text.Contains("disco cheio"))?.Text;
                Click(ButtonsOf(w).Single(b => (string)b.Content == "OK"));
            });

            Click(Find<Button>(window, "BtnSave"));

            Assert.NotNull(shown);
            Assert.True(window.IsVisible);
            Assert.Same(original, SettingsService.Current);
            Assert.False(original.StartWithWindows);
            Assert.Null(_saved);

            // Com o disco saudável, a mesma janela salva e fecha.
            AppSettings? saved = null;
            SettingsService.SaveOverrideForTests = settings => saved = settings;
            Click(Find<Button>(window, "BtnSave"));

            Assert.NotNull(saved);
            Assert.True(saved!.StartWithWindows);
            Assert.Same(saved, SettingsService.Current);
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public void Salvar_GravaTodosOsCamposEmUmaInstanciaNova()
    {
        RunSta(() =>
        {
            AppSettings original = SettingsService.Current;
            original.DeadzonePixels = 12;   // diferente do que o formulário vai salvar

            SettingsWindow window = OpenSettings();
            Find<CheckBox>(window, "ChkStartWithWindows").IsChecked = true;
            Find<CheckBox>(window, "ChkBlockFullscreen").IsChecked = false;
            Find<Slider>(window, "SliderDeadzone").Value = 33;
            Find<Slider>(window, "SliderDwell").Value = 300;
            Find<Panel>(window, "AccentChips").Children.OfType<RadioButton>()
                .Single(c => (string)c.Tag == "Violet").IsChecked = true;

            Click(Find<Button>(window, "BtnSave"));

            AppSettings saved = Assert.IsType<AppSettings>(_saved);
            Assert.NotSame(original, saved);
            Assert.True(saved.StartWithWindows);
            Assert.False(saved.BlockExclusiveFullscreen);
            Assert.Equal(33, saved.DeadzonePixels);
            Assert.Equal(300, saved.DwellMilliseconds);
            Assert.Equal("Violet", saved.Accent);
            Assert.NotSame(original.Blacklist[0], saved.Blacklist[0]);
            Assert.NotSame(original.FavoriteApps[0], saved.FavoriteApps[0]);
            Assert.Same(saved, SettingsService.Current);
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public void Cancelar_ComAlteracoes_PedeConfirmacaoENaoAlteraSettings()
    {
        RunSta(() =>
        {
            var settings = AppSettings.CreateDefault();
            settings.CustomProfiles.Add(new CustomMenuProfile
            {
                Name = "Original",
                Contexts = { RadialContextKind.Generic },
                Items = { new CustomMenuItem { Label = "Cópia", Kind = CustomMenuItemKind.Action, Action = RadialAction.CopyPath } },
            });
            SettingsService.OverrideCurrentForTests(settings);

            SettingsWindow window = OpenSettings();
            Assert.Equal("Original", Find<TextBox>(window, "TxtProfileName").Text);
            Assert.Single(Find<ListBox>(window, "ListItems").Items);

            // O rascunho é uma cópia: editar não mexe nas configurações ativas.
            Find<TextBox>(window, "TxtProfileName").Text = "Alterado";
            Assert.Equal("Original", settings.CustomProfiles[0].Name);

            OnShown(w => Click(ButtonsOf(w).Single(b => (string)b.Content == "Continuar editando")));
            window.Close();
            Assert.True(window.IsVisible);

            OnShown(w => Click(ButtonsOf(w).Single(b => (string)b.Content == "Descartar")));
            window.Close();
            Assert.False(window.IsVisible);

            Assert.Equal("Original", settings.CustomProfiles[0].Name);
            Assert.Equal("Cópia", settings.CustomProfiles[0].Items[0].Label);
            Assert.Null(_saved);
        });
    }
}
