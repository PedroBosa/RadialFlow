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
            CheckBox fileContext = Find<WrapPanel>(window, "PnlContexts").Children.OfType<CheckBox>()
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
