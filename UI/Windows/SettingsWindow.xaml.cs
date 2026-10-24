// ============================================================================
// RadialFlow - Módulo 6: Janela de Configurações
// ----------------------------------------------------------------------------
// Interface gráfica para personalização da Blacklist, Apps Favoritos,
// Sensibilidade do arrasto e Inicialização com o Windows.
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

        AboutVersion.Text = $"Versão {AppInfo.Version} · Menu radial contextual para Windows";
        LogPath.Text = AppLog.FilePath;
        AutomationProperties.SetName(ChkStartWithWindows, "Iniciar com o Windows");
        AutomationProperties.SetName(ChkBlockFullscreen, "Pausar em tela cheia exclusiva");

        _blacklistItems = new ObservableCollection<ForegroundBlacklist.BlacklistEntry>(current.Blacklist);
        ListBlacklist.ItemsSource = _blacklistItems;

        _favoriteItems = new ObservableCollection<FavoriteAppEntry>(current.FavoriteApps);
        ListFavorites.ItemsSource = _favoriteItems;
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
    }

    private void UpdateAccentLabel() =>
        AccentName.Text = RadialTheme.AccentChoices.FirstOrDefault(c => c.Key == _accentKey).Label ?? "Azul";
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
                _blacklistItems.Add(item);
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
                _favoriteItems.Add(item);
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsService.Current;

        settings.StartWithWindows = ChkStartWithWindows.IsChecked == true;
        settings.BlockExclusiveFullscreen = ChkBlockFullscreen.IsChecked == true;
        settings.DeadzonePixels = (int)SliderDeadzone.Value;
        settings.DwellMilliseconds = (int)SliderDwell.Value;
        settings.Accent = _accentKey;

        settings.Blacklist = new List<ForegroundBlacklist.BlacklistEntry>(_blacklistItems);
        settings.FavoriteApps = new List<FavoriteAppEntry>(_favoriteItems);

        SettingsService.Save(settings);

        // Aplica as novas configurações no controller ativo
        _controller.ApplySettings(settings);
        _tray.UpdateState();

        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
