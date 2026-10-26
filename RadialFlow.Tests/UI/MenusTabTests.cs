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

    private static void SelectTag(ComboBox combo, object tag) =>
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().Single(i => Equals(i.Tag, tag));

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

            Assert.Equal(6, tabs.Length);
            Assert.Equal("Apps Favoritos", tabs[2].Header);
            Assert.Equal("Menus", tabs[3].Header);
            Assert.Equal("Conversores", tabs[4].Header);
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
            Assert.False(Find<StackPanel>(window, "PnlProfileEditor").IsEnabled);

            Click(Find<Button>(window, "BtnAddMenuProfile"));
            Assert.Single(profiles.Items);
            var first = (CustomMenuProfile)profiles.Items[0];
            Assert.Equal("Novo menu", first.Name);
            Assert.Equal("Novo menu", Find<TextBox>(window, "TxtProfileName").Text);
            Assert.True(Find<StackPanel>(window, "PnlProfileEditor").IsEnabled);

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
            Assert.False(Find<StackPanel>(window, "PnlProfileEditor").IsEnabled);

            Assert.Empty(SettingsService.Current.CustomProfiles);
            CloseDiscarding(window);
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
            CheckBox fileContext = Find<UniformGrid>(window, "PnlContexts").Children.OfType<CheckBox>()
                .Single(c => Equals(c.Tag, RadialContextKind.FileOrFolder));
            fileContext.IsChecked = true;
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
            Find<UniformGrid>(window, "PnlContexts").Children.OfType<CheckBox>()
                .Single(c => Equals(c.Tag, RadialContextKind.FileOrFolder)).IsChecked = true;
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
            Find<UniformGrid>(window, "PnlContexts").Children.OfType<CheckBox>()
                .Single(c => Equals(c.Tag, RadialContextKind.FileOrFolder)).IsChecked = true;
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
            Find<TabControl>(window, "MainTabs").SelectedIndex = 3;
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
