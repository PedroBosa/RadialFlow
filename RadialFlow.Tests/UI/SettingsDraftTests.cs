using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using RadialFlow.Config;
using RadialFlow.Infrastructure;
using RadialFlow.Input;
using RadialFlow.UI;
using Xunit;

namespace RadialFlow.Tests;

/// <summary>
/// Ciclo de vida dos rascunhos das Configurações: nenhuma edição vaza para Current antes de Salvar e
/// fechar com qualquer seção alterada pede confirmação. Nada do arquivo real é lido ou gravado.
/// </summary>
[Collection("UiWindows")]
public sealed class SettingsDraftTests : IDisposable
{
    private AppSettings? _saved;

    public SettingsDraftTests()
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

    private static string? VisibleText(Window window, string contains) =>
        Descendants(window).OfType<TextBlock>()
            .FirstOrDefault(t => t.Visibility == Visibility.Visible && t.Text.Contains(contains, StringComparison.OrdinalIgnoreCase))
            ?.Text;

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

    private static RadioButton Accent(Window window, string key) =>
        Find<Panel>(window, "AccentChips").Children.OfType<RadioButton>().Single(c => (string)c.Tag == key);

    // ------------------------------------------------------------------
    // Cancelar em cada seção
    // ------------------------------------------------------------------

    [Fact]
    public void Cancelar_Geral_AvisaENaoAlteraCurrent()
    {
        RunSta(() =>
        {
            AppSettings settings = SettingsService.Current;
            int deadzone = settings.DeadzonePixels;

            SettingsWindow window = OpenSettings();
            Find<Slider>(window, "SliderDeadzone").Value = 33;

            string? shown = null;
            OnShown(w =>
            {
                shown = VisibleText(w, "Configurações não salvas");
                Click(ButtonsOf(w).Single(b => (string)b.Content == "Descartar"));
            });

            window.Close();

            Assert.StartsWith("Configurações não salvas: Geral.", shown);
            Assert.False(window.IsVisible);
            Assert.Equal(deadzone, settings.DeadzonePixels);
            Assert.Null(_saved);
        });
    }

    [Fact]
    public void Cancelar_Aparencia_AvisaENaoAlteraCurrent()
    {
        RunSta(() =>
        {
            AppSettings settings = SettingsService.Current;
            string accent = settings.Accent;

            SettingsWindow window = OpenSettings();
            Accent(window, "Violet").IsChecked = true;
            Pump(50);

            string? shown = null;
            OnShown(w =>
            {
                shown = VisibleText(w, "Configurações não salvas");
                Click(ButtonsOf(w).Single(b => (string)b.Content == "Descartar"));
            });

            window.Close();

            Assert.StartsWith("Configurações não salvas: Aparência.", shown);
            Assert.False(window.IsVisible);
            Assert.Equal(accent, settings.Accent);
            Assert.Null(_saved);
        });
    }

    [Fact]
    public void Cancelar_EstiloDaRoda_AvisaAparenciaENaoAlteraCurrent()
    {
        RunSta(() =>
        {
            AppSettings settings = SettingsService.Current;
            Assert.Equal("Standard", settings.WheelPreset);

            SettingsWindow window = OpenSettings();
            var presets = Find<ComboBox>(window, "CmbWheelPreset");
            Assert.Equal("Label", presets.DisplayMemberPath);
            Assert.Equal(WheelPreset.All.Count, presets.Items.Count);
            Assert.Same(WheelPreset.Standard, presets.SelectedItem);

            presets.SelectedItem = WheelPreset.Compact;

            string? shown = null;
            OnShown(w =>
            {
                shown = VisibleText(w, "Configurações não salvas");
                Click(ButtonsOf(w).Single(b => (string)b.Content == "Descartar"));
            });

            window.Close();

            Assert.StartsWith("Configurações não salvas: Aparência.", shown);
            Assert.False(window.IsVisible);
            Assert.Equal("Standard", settings.WheelPreset);
            Assert.Null(_saved);
        });
    }

    [Fact]
    public void Cancelar_Idioma_AvisaAparenciaENaoMudaCurrent()
    {
        RunSta(() =>
        {
            AppSettings settings = SettingsService.Current;
            Assert.Equal("pt-BR", settings.Language);

            SettingsWindow window = OpenSettings();
            var language = Find<ComboBox>(window, "CmbLanguage");
            var hint = Find<TextBlock>(window, "LanguageRestartHint");
            Assert.Equal(Visibility.Collapsed, hint.Visibility);

            language.SelectedIndex = 1;   // English (United States)
            Pump(50);

            Assert.Equal(Visibility.Visible, hint.Visibility);
            Assert.Equal("Alterações não salvas: Aparência", Find<TextBlock>(window, "SaveStatus").Text);

            string? shown = null;
            OnShown(w =>
            {
                shown = VisibleText(w, "Configurações não salvas");
                Click(ButtonsOf(w).Single(b => (string)b.Content == "Descartar"));
            });

            window.Close();

            Assert.StartsWith("Configurações não salvas: Aparência.", shown);
            Assert.False(window.IsVisible);
            Assert.Equal("pt-BR", settings.Language);
            Assert.Null(_saved);
        });
    }

    [Fact]
    public void Cancelar_EmFavoritos_AvisaENaoAlteraCurrent()
    {
        RunSta(() =>
        {
            AppSettings settings = SettingsService.Current;
            int count = settings.FavoriteApps.Count;
            FavoriteAppEntry original = settings.FavoriteApps[0];

            SettingsWindow window = OpenSettings();
            var favorites = Find<ListBox>(window, "ListFavorites");
            favorites.SelectedIndex = 0;
            Click(Find<Button>(window, "BtnRemoveFavorite"));
            Assert.Equal(count - 1, favorites.Items.Count);

            string? shown = null;
            OnShown(w =>
            {
                shown = VisibleText(w, "Configurações não salvas");
                Click(ButtonsOf(w).Single(b => (string)b.Content == "Descartar"));
            });

            window.Close();

            Assert.NotNull(shown);
            Assert.Contains("Apps Favoritos", shown);
            Assert.False(window.IsVisible);
            Assert.Equal(count, settings.FavoriteApps.Count);
            Assert.Same(original, settings.FavoriteApps[0]);
            Assert.Null(_saved);
        });
    }

    [Fact]
    public void Cancelar_EmBlacklist_AvisaENaoAlteraCurrent()
    {
        RunSta(() =>
        {
            AppSettings settings = SettingsService.Current;
            int count = settings.Blacklist.Count;
            ForegroundBlacklist.BlacklistEntry original = settings.Blacklist[0];

            SettingsWindow window = OpenSettings();
            var blacklist = Find<ListBox>(window, "ListBlacklist");
            blacklist.SelectedIndex = 0;
            Click(Find<Button>(window, "BtnRemoveBlacklist"));
            Assert.Equal(count - 1, blacklist.Items.Count);

            string? shown = null;
            OnShown(w =>
            {
                shown = VisibleText(w, "Configurações não salvas");
                Click(ButtonsOf(w).Single(b => (string)b.Content == "Descartar"));
            });

            window.Close();

            Assert.NotNull(shown);
            Assert.Contains("Blacklist", shown);
            Assert.False(window.IsVisible);
            Assert.Equal(count, settings.Blacklist.Count);
            Assert.Same(original, settings.Blacklist[0]);
            Assert.Null(_saved);
        });
    }

    // ------------------------------------------------------------------
    // Isolamento dos rascunhos
    // ------------------------------------------------------------------

    [Fact]
    public void Rascunhos_NaoCompartilhamObjetosComCurrent()
    {
        RunSta(() =>
        {
            AppSettings settings = SettingsService.Current;
            FavoriteAppEntry originalFavorite = settings.FavoriteApps[0];
            ForegroundBlacklist.BlacklistEntry originalBlack = settings.Blacklist[0];

            SettingsWindow window = OpenSettings();
            var draftFavorite = (FavoriteAppEntry)Find<ListBox>(window, "ListFavorites").Items[0];
            var draftBlack = (ForegroundBlacklist.BlacklistEntry)Find<ListBox>(window, "ListBlacklist").Items[0];

            Assert.NotSame(originalFavorite, draftFavorite);
            Assert.NotSame(originalBlack, draftBlack);

            // Mutar o rascunho não pode tocar no objeto que está em Current.
            draftFavorite.Name = "Alterado no rascunho";
            draftFavorite.ExecutablePath = @"C:\rascunho.exe";
            Assert.Equal("Bloco de Notas", originalFavorite.Name);
            Assert.Equal("notepad.exe", originalFavorite.ExecutablePath);
            Assert.DoesNotContain(draftFavorite, settings.FavoriteApps);

            CloseDiscarding(window);
        });
    }

    [Fact]
    public void Salvar_PersisteCopiasENaoPedeConfirmacaoAoFechar()
    {
        RunSta(() =>
        {
            AppSettings settings = SettingsService.Current;
            string removedName = settings.FavoriteApps[0].Name;
            List<FavoriteAppEntry> originalFavorites = settings.FavoriteApps.ToList();
            List<ForegroundBlacklist.BlacklistEntry> originalBlacklist = settings.Blacklist.ToList();

            SettingsWindow window = OpenSettings();
            Find<ListBox>(window, "ListFavorites").SelectedIndex = 0;
            Click(Find<Button>(window, "BtnRemoveFavorite"));

            Dialogs.BeforeShow = _ => throw new InvalidOperationException("Salvar com sucesso não deve pedir confirmação.");

            Click(Find<Button>(window, "BtnSave"));

            AppSettings saved = Assert.IsType<AppSettings>(_saved);
            Assert.False(window.IsVisible);
            Assert.Equal(originalFavorites.Count - 1, saved.FavoriteApps.Count);
            Assert.DoesNotContain(saved.FavoriteApps, f => f.Name == removedName);
            Assert.All(saved.FavoriteApps, f => Assert.DoesNotContain(f, originalFavorites));
            Assert.Equal(originalBlacklist.Count, saved.Blacklist.Count);
            for (int i = 0; i < saved.Blacklist.Count; i++)
                Assert.NotSame(originalBlacklist[i], saved.Blacklist[i]);
            Assert.Same(saved, SettingsService.Current);

            // O objeto antigo permanece intacto.
            Assert.Equal(originalFavorites.Count, settings.FavoriteApps.Count);
        });
    }

    [Fact]
    public void Salvar_Idioma_PersisteEMostraAvisoDeReinicio()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            var language = Find<ComboBox>(window, "CmbLanguage");
            language.SelectedIndex = 1;   // English (United States)
            Pump(50);

            Assert.Equal(Visibility.Visible, Find<TextBlock>(window, "LanguageRestartHint").Visibility);

            string? message = null;
            OnShown(w =>
            {
                message = VisibleText(w, Loc.Get("Settings.RestartRequired"));
                Click(ButtonsOf(w).Single(b => (string)b.Content == "OK"));
            });

            Click(Find<Button>(window, "BtnSave"));

            AppSettings saved = Assert.IsType<AppSettings>(_saved);
            Assert.Equal("en-US", saved.Language);
            Assert.Same(saved, SettingsService.Current);
            Assert.False(window.IsVisible);
            Assert.Equal(Loc.Get("Settings.RestartRequired"), message);
        });
    }

    [Fact]
    public void Salvar_EstiloDaRoda_PersisteAEscolha()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            var presets = Find<ComboBox>(window, "CmbWheelPreset");

            presets.SelectedItem = WheelPreset.HighContrast;
            Assert.Contains("opacas", Find<TextBlock>(window, "WheelPresetHint").Text);

            Dialogs.BeforeShow = _ => throw new InvalidOperationException("Salvar com sucesso não deve pedir confirmação.");
            Click(Find<Button>(window, "BtnSave"));

            AppSettings saved = Assert.IsType<AppSettings>(_saved);
            Assert.Equal("HighContrast", saved.WheelPreset);
            Assert.Same(saved, SettingsService.Current);
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public void FalhaDeGravacao_MantemEstadoECobraConfirmacaoAoFechar()
    {
        RunSta(() =>
        {
            AppSettings settings = SettingsService.Current;
            int deadzone = settings.DeadzonePixels;
            SettingsService.SaveOverrideForTests = _ => throw new IOException("disco cheio");

            SettingsWindow window = OpenSettings();
            Find<Slider>(window, "SliderDeadzone").Value = 33;

            string? error = null;
            OnShown(w =>
            {
                error = VisibleText(w, "disco cheio");
                Click(ButtonsOf(w).Single(b => (string)b.Content == "OK"));
            });

            Click(Find<Button>(window, "BtnSave"));

            Assert.NotNull(error);
            Assert.True(window.IsVisible);
            Assert.Same(settings, SettingsService.Current);
            Assert.Equal(deadzone, settings.DeadzonePixels);

            // A alteração continua pendente: fechar ainda pede confirmação.
            string? shown = null;
            OnShown(w =>
            {
                shown = VisibleText(w, "Configurações não salvas");
                Click(ButtonsOf(w).Single(b => (string)b.Content == "Descartar"));
            });

            window.Close();

            Assert.NotNull(shown);
            Assert.Contains("Geral", shown);
            Assert.False(window.IsVisible);
            Assert.Equal(deadzone, settings.DeadzonePixels);
            Assert.Null(_saved);
        });
    }

    // ------------------------------------------------------------------
    // Aviso único
    // ------------------------------------------------------------------

    [Fact]
    public void Fechar_ComVariasSecoes_MostraUmUnicoAvisoComTodas()
    {
        RunSta(() =>
        {
            SettingsWindow window = OpenSettings();
            Find<Slider>(window, "SliderDeadzone").Value = 33;
            Click(Find<Button>(window, "BtnAddMenuProfile"));

            int dialogs = 0;
            string? shown = null;
            OnShown(w =>
            {
                dialogs++;
                shown = VisibleText(w, "Configurações não salvas");
                Click(ButtonsOf(w).Single(b => (string)b.Content == "Continuar editando"));
            });

            window.Close();

            Assert.Equal(1, dialogs);
            Assert.True(window.IsVisible);
            Assert.NotNull(shown);
            Assert.Contains("Geral", shown);
            Assert.Contains("Menus", shown);
            Assert.True(shown.IndexOf("Geral", StringComparison.Ordinal) < shown.IndexOf("Menus", StringComparison.Ordinal));

            OnShown(w => Click(ButtonsOf(w).Single(b => (string)b.Content == "Descartar")));
            window.Close();

            Assert.False(window.IsVisible);
            Assert.Empty(SettingsService.Current.CustomProfiles);
            Assert.Null(_saved);
        });
    }
}
