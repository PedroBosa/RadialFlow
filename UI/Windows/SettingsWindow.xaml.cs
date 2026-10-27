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

        AboutVersion.Text = $"Versão {AppInfo.Version} · Menu radial contextual para Windows";
        LogPath.Text = AppLog.FilePath;
        AutomationProperties.SetName(ChkStartWithWindows, "Iniciar com o Windows");
        AutomationProperties.SetName(ChkBlockFullscreen, "Pausar em tela cheia exclusiva");

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

        _nonMenuBaseline = CaptureNonMenuSnapshot();
        _ready = true;
        UpdateDirtyIndicator();
    }

    private NonMenuSnapshot? _nonMenuBaseline;
    private bool _ready;

    /// <summary>Valores não-menu na abertura; o fechamento compara com eles para avisar sobre descarte.</summary>
    private sealed record NonMenuSnapshot(
        bool StartWithWindows,
        bool BlockExclusiveFullscreen,
        int DeadzonePixels,
        int DwellMilliseconds,
        string Accent,
        string WheelPreset,
        IReadOnlyList<(string ProcessName, string Description)> Blacklist,
        IReadOnlyList<(string Name, string ExecutablePath, string Arguments, string Glyph)> FavoriteApps);

    private NonMenuSnapshot CaptureNonMenuSnapshot() => new(
        ChkStartWithWindows.IsChecked == true,
        ChkBlockFullscreen.IsChecked == true,
        (int)SliderDeadzone.Value,
        (int)SliderDwell.Value,
        _accentKey,
        _wheelPreset.Key,
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
                !string.Equals(now.Accent, baseline.Accent, StringComparison.Ordinal) ||
                !string.Equals(now.WheelPreset, baseline.WheelPreset, StringComparison.Ordinal))
            {
                changed.Add("Geral");
            }

            if (!now.FavoriteApps.SequenceEqual(baseline.FavoriteApps)) changed.Add("Apps Favoritos");
            if (!now.Blacklist.SequenceEqual(baseline.Blacklist)) changed.Add("Blacklist");
        }

        if (_menusDirty) changed.Add("Menus");
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
        BlacklistCount.Text = _blacklistItems.Count == 1 ? "1 programa" : $"{_blacklistItems.Count} programas";
        FavoritesCount.Text = _favoriteItems.Count == 1 ? "1 atalho" : $"{_favoriteItems.Count} atalhos";
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
            ? $"Alterações não salvas: {string.Join(", ", changed)}"
            : "Tudo salvo";
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
            Dialogs.Message(this, "Área de transferência", "A área de transferência está em uso por outro programa. Tente de novo.", DialogIcon.Warning);
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
            AutomationProperties.SetName(chip, $"Cor de destaque: {label}");
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
        AccentName.Text = RadialTheme.AccentChoices.FirstOrDefault(c => c.Key == _accentKey).Label ?? "Azul";

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

    private void BtnAddBlacklist_Click(object sender, RoutedEventArgs e)
    {
        var entry = Dialogs.PromptPair(this, "Adicionar à blacklist",
            "Nome do processo (ex.: blender, acad, maya)", "",
            "Descrição (opcional)", "",
            validate: (process, _) =>
            {
                string name = NormalizeProcessName(process);
                if (name.Length == 0) return "Informe o nome do processo.";
                if (name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0) return "Use só o nome do processo, sem pasta nem caracteres especiais.";
                if (_blacklistItems.Any(i => string.Equals(i.ProcessName, name, StringComparison.OrdinalIgnoreCase))) return $"'{name}' já está na lista.";
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
        if (Dialogs.Confirm(this, "Restaurar blacklist", "Restaurar a lista padrão de programas ignorados? A lista atual será substituída.", "Restaurar", "Cancelar"))
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
            Title = "Selecionar Executável do Aplicativo",
            Filter = "Executáveis (*.exe)|*.exe|Todos os arquivos (*.*)|*.*"
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
        if (Dialogs.Confirm(this, "Restaurar apps favoritos", "Restaurar a lista padrão de apps favoritos? A lista atual será substituída.", "Restaurar", "Cancelar"))
        {
            _favoriteItems.Clear();
            var def = AppSettings.CreateDefault();
            foreach (var item in def.FavoriteApps)
                _favoriteItems.Add(new FavoriteAppEntry(item.Name, item.ExecutablePath, item.Arguments, item.Glyph));
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        string? menuError = ValidateMenus();
        if (menuError is not null)
        {
            Dialogs.Message(this, "Menus personalizados", menuError, DialogIcon.Warning);
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
            Blacklist = _blacklistItems
                .Select(e => new ForegroundBlacklist.BlacklistEntry(e.ProcessName, e.Description))
                .ToList(),
            FavoriteApps = _favoriteItems
                .Select(a => new FavoriteAppEntry(a.Name, a.ExecutablePath, a.Arguments, a.Glyph))
                .ToList(),
        };
        ApplyMenusTo(settings);

        if (!SettingsService.TrySave(settings, out string? error))
        {
            Dialogs.Message(this, "Não foi possível salvar",
                error ?? "Não foi possível salvar as configurações.", DialogIcon.Error);
            return;
        }

        // Só depois de o disco aceitar: aplica no controller ativo e fecha.
        _controller?.ApplySettings(settings);
        _tray?.UpdateState();

        _menusDirty = false;
        _nonMenuBaseline = CaptureNonMenuSnapshot();   // salvo: fechar sem aviso de descarte
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
