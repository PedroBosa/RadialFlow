// ============================================================================
// RadialFlow - Janela de Configurações
// ----------------------------------------------------------------------------
// Interface gráfica para personalização da Blacklist, Apps Favoritos, Menus
// personalizados, Sensibilidade do arrasto e Inicialização com o Windows.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using RadialFlow.Config;
using RadialFlow.Infrastructure;
using RadialFlow.Input;

namespace RadialFlow.UI;

public partial class SettingsWindow : Window
{
    private readonly RadialController _controller;
    private readonly TrayIconService _tray;

    private readonly ObservableCollection<ForegroundBlacklist.BlacklistEntry> _blacklistItems;
    private readonly ObservableCollection<FavoriteAppEntry> _favoriteItems;

    public SettingsWindow(RadialController controller, TrayIconService tray)
    {
        UiTheme.Install(this);        // estilos e cores: antes do XAML, que usa DynamicResource
        UiTheme.StyleTitleBar(this);
        InitializeComponent();
        Icon = AppIconRenderer.WindowIcon;

        _controller = controller;
        _tray = tray;

        var current = SettingsService.Current;

        ChkStartWithWindows.IsChecked = current.StartWithWindows;
        ChkBlockFullscreen.IsChecked = current.BlockExclusiveFullscreen;
        SliderDeadzone.Value = current.DeadzonePixels;
        SliderDwell.Value = Math.Clamp(current.DwellMilliseconds, RadialMenuWindow.MinDwellMilliseconds, RadialMenuWindow.MaxDwellMilliseconds);
        FillAccentChoices(current.Accent);
        FillWheelPresets(current.WheelPreset);
        FillLanguages(current.Language);
        FillHotkey(current.Hotkey);

        AboutVersion.Text = Loc.Format("About.VersionFormat", AppInfo.Version);
        LogPath.Text = AppLog.FilePath;

        // Rascunhos: listas próprias, sem compartilhar os objetos de Current.
        _blacklistItems = new ObservableCollection<ForegroundBlacklist.BlacklistEntry>(
            current.Blacklist.Select(e => new ForegroundBlacklist.BlacklistEntry(e.ProcessName, e.Description)));
        _favoriteItems = new ObservableCollection<FavoriteAppEntry>(
            current.FavoriteApps.Select(a => new FavoriteAppEntry(a.Name, a.ExecutablePath, a.Arguments, a.Glyph)));
        ListBlacklist.ItemsSource = _blacklistItems;
        ListFavorites.ItemsSource = _favoriteItems;

        _blacklistItems.CollectionChanged += (_, _) => OnListsChanged();
        _favoriteItems.CollectionChanged += (_, _) => OnListsChanged();
        ListBlacklist.SelectionChanged += (_, _) => UpdateListToolbar();
        ListFavorites.SelectionChanged += (_, _) => UpdateListToolbar();
        UpdateListToolbar();

        InitializeConvertersTab();
        InitializeMenusTab();

        MainTabs.SelectionChanged += (_, _) =>
        {
            if (_recordingHotkey) StopRecordingHotkey();
        };

        _nonMenuBaseline = CaptureNonMenuSnapshot();
        _ready = true;
        UpdateDirtyIndicator();
    }

    private NonMenuSnapshot? _nonMenuBaseline;
    private bool _ready;
    private string _hotkey = "";
    private bool _recordingHotkey;

    /// <summary>Valores não-menu na abertura; o fechamento compara com eles para avisar sobre descarte.</summary>
    private sealed record NonMenuSnapshot(
        bool StartWithWindows,
        bool BlockExclusiveFullscreen,
        int DeadzonePixels,
        int DwellMilliseconds,
        string Accent,
        string WheelPreset,
        string Language,
        string Hotkey,
        IReadOnlyList<(string ProcessName, string Description)> Blacklist,
        IReadOnlyList<(string Name, string ExecutablePath, string Arguments, string Glyph)> FavoriteApps);

    private NonMenuSnapshot CaptureNonMenuSnapshot() => new(
        ChkStartWithWindows.IsChecked == true,
        ChkBlockFullscreen.IsChecked == true,
        (int)SliderDeadzone.Value,
        (int)SliderDwell.Value,
        _accentKey,
        _wheelPreset.Key,
        _language,
        _hotkey,
        _blacklistItems.Select(e => (e.ProcessName, e.Description)).ToArray(),
        _favoriteItems.Select(a => (a.Name, a.ExecutablePath, a.Arguments, a.Glyph)).ToArray());

    /// <summary>Seções com alterações não salvas, na ordem exibida no aviso de descarte.</summary>
    private List<string> ChangedSections()
    {
        var changed = new List<string>();

        if (_nonMenuBaseline is { } baseline)
        {
            NonMenuSnapshot now = CaptureNonMenuSnapshot();
            if (now.StartWithWindows != baseline.StartWithWindows ||
                now.BlockExclusiveFullscreen != baseline.BlockExclusiveFullscreen ||
                now.DeadzonePixels != baseline.DeadzonePixels ||
                now.DwellMilliseconds != baseline.DwellMilliseconds ||
                !string.Equals(now.Hotkey, baseline.Hotkey, StringComparison.Ordinal))
            {
                changed.Add(Loc.Get("Settings.General"));
            }

            if (!string.Equals(now.Accent, baseline.Accent, StringComparison.Ordinal) ||
                !string.Equals(now.WheelPreset, baseline.WheelPreset, StringComparison.Ordinal) ||
                !string.Equals(now.Language, baseline.Language, StringComparison.Ordinal))
            {
                changed.Add(Loc.Get("Settings.Appearance"));
            }

            if (!now.Blacklist.SequenceEqual(baseline.Blacklist)) changed.Add(Loc.Get("Settings.Blacklist"));
            if (!now.FavoriteApps.SequenceEqual(baseline.FavoriteApps)) changed.Add(Loc.Get("Settings.Favorites"));
        }

        if (_menusDirty) changed.Add(Loc.Get("Settings.Menus"));
        return changed;
    }

    private void DirtyState_Changed(object sender, RoutedEventArgs e) => UpdateDirtyIndicator();

    private void OnListsChanged()
    {
        UpdateListToolbar();
        UpdateDirtyIndicator();
    }

    private void UpdateListToolbar()
    {
        BlacklistCount.Text = _blacklistItems.Count == 1
            ? Loc.Get("Common.ProgramsCountOne")
            : Loc.Format("Common.ProgramsCountMany", _blacklistItems.Count);
        FavoritesCount.Text = _favoriteItems.Count == 1
            ? Loc.Get("Common.ShortcutsCountOne")
            : Loc.Format("Common.ShortcutsCountMany", _favoriteItems.Count);
        PnlNoBlacklist.Visibility = _blacklistItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PnlNoFavorites.Visibility = _favoriteItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnRemoveBlacklist.IsEnabled = ListBlacklist.SelectedItem is ForegroundBlacklist.BlacklistEntry;
        BtnRemoveFavorite.IsEnabled = ListFavorites.SelectedItem is FavoriteAppEntry;
    }

    private void UpdateDirtyIndicator()
    {
        if (!_ready) return;

        List<string> changed = ChangedSections();
        bool dirty = changed.Count > 0;
        StatusDot.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        SaveStatus.Text = dirty
            ? Loc.Format("Settings.Unsaved", string.Join(", ", changed))
            : Loc.Get("Settings.AllSaved");
    }

    private void BtnOpenLogs_Click(object sender, RoutedEventArgs e) => TrayIconService.OpenLogsFolder();

    private void BtnCopyLogPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(AppLog.FilePath);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // Outro programa está usando a área de transferência neste instante.
            AppLog.Warning("Settings", "Não foi possível copiar o caminho do log.", ex);
            Dialogs.Message(this, Loc.Get("Settings.ClipboardTitle"), Loc.Get("Settings.ClipboardBusy"), DialogIcon.Warning);
        }
    }

    private string _accentKey = "Blue";

    /// <summary>
    /// Uma bolinha por cor. Ao escolher, o próprio formulário já muda de cor (prévia ao vivo);
    /// a roda só passa a usar a cor depois de Salvar.
    /// </summary>
    private void FillAccentChoices(string? saved)
    {
        _accentKey = RadialTheme.AccentChoices
            .Select(c => c.Key)
            .FirstOrDefault(k => string.Equals(k, saved?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "Blue";

        foreach (var (key, label, color) in RadialTheme.AccentChoices)
        {
            Color shown = color ?? RadialTheme.ReadAccent();   // "Cor do Windows" mostra a cor atual do sistema
            var chip = new RadioButton
            {
                GroupName = "accent",
                Style = (Style)FindResource("AccentChip"),
                Background = new SolidColorBrush(shown),
                Foreground = new SolidColorBrush(ColorMath.PickInk(shown)),   // o "✓" sempre legível sobre a cor
                Tag = key,
                ToolTip = label,
                IsChecked = key == _accentKey
            };
            chip.Checked += OnAccentChecked;
            AutomationProperties.SetName(chip, Loc.Format("Settings.Appearance.AccentAutomation", label));
            AccentChips.Children.Add(chip);
        }

        UpdateAccentLabel();
    }

    private void OnAccentChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string key }) return;

        _accentKey = key;
        UpdateAccentLabel();
        UiTheme.Refresh(this, RadialTheme.Create(RadialTheme.ReadLightTheme(), RadialTheme.ResolveAccent(key)));
        UpdateDirtyIndicator();
    }

    private void UpdateAccentLabel() =>
        AccentName.Text = RadialTheme.AccentChoices.FirstOrDefault(c => c.Key == _accentKey).Label ?? Loc.Get("Accent.Blue");

    private WheelPreset _wheelPreset = WheelPreset.Standard;

    private void FillWheelPresets(string? saved)
    {
        CmbWheelPreset.ItemsSource = WheelPreset.All;
        _wheelPreset = WheelPreset.Resolve(saved);
        CmbWheelPreset.SelectedItem = _wheelPreset;
        WheelPresetHint.Text = _wheelPreset.Description;
    }

    private void WheelPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CmbWheelPreset.SelectedItem is not WheelPreset preset) return;

        _wheelPreset = preset;
        WheelPresetHint.Text = preset.Description;
        UpdateDirtyIndicator();
    }

    private string _language = Loc.DefaultLanguage;

    private void FillLanguages(string? saved)
    {
        _language = Loc.Normalize(saved);
        CmbLanguage.ItemsSource = Loc.Languages;
        CmbLanguage.SelectedItem = Loc.Languages.FirstOrDefault(l => l.Key == _language);
        UpdateLanguageRestartHint();
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CmbLanguage.SelectedItem is not LanguageOption option) return;

        _language = option.Key;
        UpdateLanguageRestartHint();
        UpdateDirtyIndicator();
    }

    /// <summary>Só quando a escolha difere do que está salvo: o idioma novo só entra no próximo início.</summary>
    private void UpdateLanguageRestartHint()
    {
        bool changed = !string.Equals(_language, Loc.Normalize(SettingsService.Current.Language), StringComparison.Ordinal);
        LanguageRestartHint.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnAddBlacklist_Click(object sender, RoutedEventArgs e)
    {
        var entry = Dialogs.PromptPair(this, Loc.Get("Settings.Blacklist.AddTitle"),
            Loc.Get("Settings.Blacklist.ProcessPrompt"), "",
            Loc.Get("Common.DescriptionOptional"), "",
            validate: (process, _) =>
            {
                string name = NormalizeProcessName(process);
                if (name.Length == 0) return Loc.Get("Settings.Blacklist.ProcessRequired");
                if (name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0) return Loc.Get("Settings.Blacklist.ProcessInvalid");
                if (_blacklistItems.Any(i => string.Equals(i.ProcessName, name, StringComparison.OrdinalIgnoreCase))) return Loc.Format("Settings.Blacklist.AlreadyListed", name);
                return null;
            });

        if (entry is not { } value) return;

        string processName = NormalizeProcessName(value.First);
        string description = string.IsNullOrWhiteSpace(value.Second) ? processName : value.Second.Trim();
        _blacklistItems.Add(new ForegroundBlacklist.BlacklistEntry(processName, description));
    }

    /// <summary>"Blender.EXE " → "Blender": sem espaços nas pontas e sem a extensão .exe.</summary>
    internal static string NormalizeProcessName(string? input)
    {
        string name = (input ?? "").Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4].Trim() : name;
    }
    private void BtnRemoveBlacklist_Click(object sender, RoutedEventArgs e)
    {
        if (ListBlacklist.SelectedItem is ForegroundBlacklist.BlacklistEntry entry)
        {
            _blacklistItems.Remove(entry);
        }
    }

    private void BtnResetBlacklist_Click(object sender, RoutedEventArgs e)
    {
        if (Dialogs.Confirm(this, Loc.Get("Settings.Blacklist.ResetTitle"), Loc.Get("Settings.Blacklist.ResetPrompt"),
                Loc.Get("Common.Restore"), Loc.Get("Common.Cancel")))
        {
            _blacklistItems.Clear();
            foreach (var item in ForegroundBlacklist.DefaultEntries)
                _blacklistItems.Add(new ForegroundBlacklist.BlacklistEntry(item.ProcessName, item.Description));
        }
    }

    private void BtnAddFavorite_Click(object sender, RoutedEventArgs e)
    {
        var openDialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.Get("Settings.Favorites.PickExecutable"),
            Filter = Loc.Get("Settings.Favorites.ExecutableFilter")
        };

        if (openDialog.ShowDialog(this) == true)
        {
            string path = openDialog.FileName;
            string fileName = Path.GetFileNameWithoutExtension(path);

            _favoriteItems.Add(new FavoriteAppEntry(fileName, path));
        }
    }

    private void BtnRemoveFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (ListFavorites.SelectedItem is FavoriteAppEntry entry)
        {
            _favoriteItems.Remove(entry);
        }
    }

    private void BtnResetFavorites_Click(object sender, RoutedEventArgs e)
    {
        if (Dialogs.Confirm(this, Loc.Get("Settings.Favorites.ResetTitle"), Loc.Get("Settings.Favorites.ResetPrompt"),
                Loc.Get("Common.Restore"), Loc.Get("Common.Cancel")))
        {
            _favoriteItems.Clear();
            var def = AppSettings.CreateDefault();
            foreach (var item in def.FavoriteApps)
                _favoriteItems.Add(new FavoriteAppEntry(item.Name, item.ExecutablePath, item.Arguments, item.Glyph));
        }
    }

    // ------------------------------------------------------------------
    // Atalho global
    // ------------------------------------------------------------------

    private void FillHotkey(string? hotkey)
    {
        _hotkey = HotkeyGesture.Normalize(hotkey);
        UpdateHotkeyDisplay();
        UpdateHotkeyStatus();
    }

    internal string HotkeyText => _hotkey;
    internal bool IsRecordingHotkey => _recordingHotkey;

    private void BtnRecordHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingHotkey) StopRecordingHotkey();
        else StartRecordingHotkey();
    }

    private void StartRecordingHotkey()
    {
        _recordingHotkey = true;
        _controller?.BeginHotkeyCapture();
        BtnRecordHotkey.Content = Loc.Get("Settings.General.HotkeyRecording");
        HotkeyDisplay.Text = Loc.Get("Settings.General.HotkeyRecording");
        ShowHotkeyStatus(Loc.Get("Settings.General.HotkeyRecordingHint"), isError: false);
        BtnRecordHotkey.Focus();
    }

    private void StopRecordingHotkey()
    {
        if (!_recordingHotkey) return;

        _recordingHotkey = false;
        _controller?.EndHotkeyCapture();
        BtnRecordHotkey.Content = Loc.Get("Settings.General.HotkeyRecord");
        UpdateHotkeyDisplay();
        UpdateHotkeyStatus();
    }

    private void BtnClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        StopRecordingHotkey();
        if (_hotkey.Length == 0) return;

        _hotkey = "";
        UpdateHotkeyDisplay();
        UpdateHotkeyStatus();
        UpdateDirtyIndicator();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_recordingHotkey) StopRecordingHotkey();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recordingHotkey) return;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.System) key = Key.LeftAlt;   // Alt sozinho chega como Key.System sem SystemKey
        e.Handled = CaptureHotkey(key, Keyboard.Modifiers);
    }

    /// <summary>Processa uma tecla durante a gravação. Devolve true quando a tecla foi consumida.</summary>
    internal bool CaptureHotkey(Key key, ModifierKeys modifiers)
    {
        if (!_recordingHotkey) return false;

        switch (HotkeyCapture.TryCapture(key, modifiers, out HotkeyGesture gesture))
        {
            case HotkeyCaptureResult.Captured:
                _hotkey = gesture.ToString();
                StopRecordingHotkey();
                UpdateDirtyIndicator();
                return true;

            case HotkeyCaptureResult.ModifierOnly:
                string preview = HotkeyCapture.DescribeModifiers(modifiers);
                HotkeyDisplay.Text = preview.Length > 0 ? preview + "+…" : Loc.Get("Settings.General.HotkeyRecording");
                return true;

            case HotkeyCaptureResult.MissingModifier:
                ShowHotkeyStatus(Loc.Get("Settings.General.HotkeyNeedModifier"), isError: true);
                return true;

            case HotkeyCaptureResult.Unsupported:
                ShowHotkeyStatus(Loc.Get("Settings.General.HotkeyUnsupported"), isError: true);
                return true;

            case HotkeyCaptureResult.Cancelled:
                StopRecordingHotkey();
                return true;

            default:
                return true;
        }
    }

    private void UpdateHotkeyDisplay() =>
        HotkeyDisplay.Text = _hotkey.Length > 0 ? _hotkey : Loc.Get("Settings.General.HotkeyNone");

    private void UpdateHotkeyStatus()
    {
        if (_hotkey.Length > 0 && _controller is { } controller &&
            controller.HotkeyError != HotkeyRegistrationError.None)
        {
            ShowHotkeyStatus(HotkeyErrorText(controller.HotkeyError), isError: true);
            return;
        }

        HotkeyStatus.Visibility = Visibility.Collapsed;
    }

    private void ShowHotkeyStatus(string text, bool isError)
    {
        HotkeyStatus.Text = text;
        HotkeyStatus.Foreground = (Brush)FindResource(isError ? "WarningBrush" : "TextDimBrush");
        HotkeyStatus.Visibility = Visibility.Visible;
    }

    private static string HotkeyErrorText(HotkeyRegistrationError error) => error switch
    {
        HotkeyRegistrationError.AlreadyInUse => Loc.Get("Settings.General.HotkeyInUse"),
        HotkeyRegistrationError.Invalid => Loc.Get("Settings.General.HotkeyInvalid"),
        _ => Loc.Get("Settings.General.HotkeyUnavailable")
    };

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingHotkey) StopRecordingHotkey();   // a combinação ainda em gravação não vale como rascunho

        string? menuError = ValidateMenus();
        if (menuError is not null)
        {
            Dialogs.Message(this, Loc.Get("Menus.PageTitle"), menuError, DialogIcon.Warning);
            return;
        }

        // Instância nova a partir dos rascunhos: Current só muda quando o disco confirmar a gravação.
        var settings = new AppSettings
        {
            StartWithWindows = ChkStartWithWindows.IsChecked == true,
            BlockExclusiveFullscreen = ChkBlockFullscreen.IsChecked == true,
            DeadzonePixels = (int)SliderDeadzone.Value,
            DwellMilliseconds = (int)SliderDwell.Value,
            Accent = _accentKey,
            WheelPreset = _wheelPreset.Key,
            Language = _language,
            Hotkey = _hotkey,
            Blacklist = _blacklistItems
                .Select(e => new ForegroundBlacklist.BlacklistEntry(e.ProcessName, e.Description))
                .ToList(),
            FavoriteApps = _favoriteItems
                .Select(a => new FavoriteAppEntry(a.Name, a.ExecutablePath, a.Arguments, a.Glyph))
                .ToList(),
        };
        ApplyMenusTo(settings);

        string normalizedHotkey = HotkeyGesture.Normalize(settings.Hotkey);
        if (settings.Hotkey.Length > 0 && normalizedHotkey.Length == 0)
        {
            ShowHotkeyStatus(Loc.Get("Settings.General.HotkeyInvalid"), isError: true);
            Dialogs.Message(this, Loc.Get("Settings.General.HotkeyErrorTitle"),
                Loc.Get("Settings.General.HotkeyInvalid"), DialogIcon.Warning);
            return;
        }
        settings.Hotkey = normalizedHotkey;

        bool hotkeyChanged = _nonMenuBaseline is { } hotkeyBaseline &&
            !string.Equals(settings.Hotkey, hotkeyBaseline.Hotkey, StringComparison.Ordinal);
        string previousHotkey = _controller?.Hotkey ?? "";

        // Registra o atalho ANTES de gravar: conflito com outro programa não pode virar um arquivo
        // que só falha no próximo início. Sem mudança, um conflito pré-existente não bloqueia o Save.
        if (hotkeyChanged && _controller is not null)
        {
            HotkeyRegistrationError hotkeyError = _controller.TrySetHotkey(settings.Hotkey, out string normalized);
            if (hotkeyError != HotkeyRegistrationError.None)
            {
                ShowHotkeyStatus(HotkeyErrorText(hotkeyError), isError: true);
                Dialogs.Message(this, Loc.Get("Settings.General.HotkeyErrorTitle"),
                    HotkeyErrorText(hotkeyError), DialogIcon.Warning);
                return;
            }
            settings.Hotkey = normalized;
        }

        bool languageChanged = _nonMenuBaseline is { } baseline &&
            !string.Equals(settings.Language, baseline.Language, StringComparison.Ordinal);

        if (!SettingsService.TrySave(settings, out string? error))
        {
            // O atalho novo já estava registrado; devolve o anterior para o app não ficar diferente do arquivo.
            if (hotkeyChanged && _controller is not null)
                _controller.TrySetHotkey(previousHotkey, out _);

            Dialogs.Message(this, Loc.Get("Settings.SaveFailedTitle"),
                error ?? Loc.Get("Settings.SaveFailed"), DialogIcon.Error);
            return;
        }

        // Só depois de o disco aceitar: aplica no controller ativo e fecha.
        _controller?.ApplySettings(settings);
        _tray?.UpdateState();

        _menusDirty = false;
        _nonMenuBaseline = CaptureNonMenuSnapshot();   // salvo: fechar sem aviso de descarte

        if (languageChanged)
            Dialogs.Message(this, Loc.Get("Settings.Language"), Loc.Get("Settings.RestartRequired"), DialogIcon.Info);

        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
