// ============================================================================
// RadialFlow - Configurações: aba Menus
// ----------------------------------------------------------------------------
// Editor dos menus personalizados: perfis com condições (contexto, processo,
// título), itens que executam ações existentes ou alvos seguros e uma prévia
// da roda. O rascunho é uma cópia profunda; só Salvar devolve ao
// SettingsService (o normalizador revalida tudo no próximo carregamento).
//
// A prévia usa o mesmo Planejamento do CustomMenuResolver, com fatias
// automáticas representativas do contexto escolhido (sem tocar em disco).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using RadialFlow.Config;
using RadialFlow.Context;
using RadialFlow.Infrastructure;
using RadialFlow.Input;
using RadialFlow.Services;

namespace RadialFlow.UI;

/// <summary>Item do seletor de ícones (par de glifo Segoe + nome amigável).</summary>
public sealed record GlyphChoice(string Glyph, string Label);

public partial class SettingsWindow
{
    private const string DefaultGlyph = "\uE734";

    private static readonly (RadialContextKind Kind, string LabelKey)[] ContextChoices =
    [
        (RadialContextKind.FileOrFolder, "Menus.ContextFileOrFolder"),
        (RadialContextKind.DesktopBackground, "Menus.ContextDesktop"),
        (RadialContextKind.TitleBar, "Menus.ContextTitleBar"),
        (RadialContextKind.TextField, "Menus.ContextTextField"),
        (RadialContextKind.Generic, "Menus.ContextGeneric"),
    ];

    // Ordem da prévia: contextos específicos primeiro; "Genérico" só quando nenhum outro foi marcado.
    private static readonly RadialContextKind[] PreviewOrder =
    [
        RadialContextKind.FileOrFolder,
        RadialContextKind.DesktopBackground,
        RadialContextKind.TitleBar,
        RadialContextKind.TextField,
        RadialContextKind.Generic,
    ];

    private readonly ObservableCollection<GlyphChoice> _glyphChoices =
        new(CustomMenuChoices.Glyphs.Select(g => new GlyphChoice(g.Glyph, g.Label)));

    private readonly List<ToggleButton> _contextChips = new();
    private ToggleButton? _contextAllChip;
    private bool _syncingContexts;

    private ObservableCollection<CustomMenuProfile> _menuProfiles = new();
    private readonly ObservableCollection<CustomMenuItem> _menuItems = new();
    private CustomMenuProfile? _selectedProfile;
    private CustomMenuItem? _selectedItem;
    private MenuPreview? _preview;
    private bool _loadingMenus;
    private bool _menusDirty;
    private DispatcherTimer? _previewTimer;

    private ICollectionView? _profileView;
    private ICollectionView? _itemView;
    private string _profileSearch = "";
    private string _itemSearch = "";
    private string _profileIssueSignature = "";

    private Point _dragStart;
    private CustomMenuItem? _dragItem;

    private const int PreviewDebounceMilliseconds = 120;

    /// <summary>Só para testes: substitui os diálogos de arquivo (null devolve "cancelado").</summary>
    internal static Func<string?>? ChooseImportPathForTests { get; set; }
    internal static Func<string?>? ChooseExportPathForTests { get; set; }
    internal static Func<string?>? ChooseExportProfilePathForTests { get; set; }

    /// <summary>Só para testes: o teclado real não está disponível para simular o Alt.</summary>
    internal static Func<ModifierKeys>? KeyboardModifiersForTests { get; set; }

    // Dependências fixas (nada lê configurações nem disco): as fatias automáticas de cada contexto não mudam.
    private static readonly Dictionary<(RadialContextKind Kind, string Language), IReadOnlyList<RadialSlice>> AutomaticPreviewCache = new();

    private void InitializeMenusTab()
    {
        _loadingMenus = true;
        try
        {
            _preview = new MenuPreview();
            MenuPreviewHost.Child = _preview;
            CmbItemGlyph.ItemsSource = _glyphChoices;

            foreach (var (kind, label) in CustomMenuChoices.Kinds)
                CmbItemKind.Items.Add(new ComboBoxItem { Content = label, Tag = kind });

            foreach (var (action, label) in CustomMenuChoices.Actions)
                CmbItemAction.Items.Add(new ComboBoxItem { Content = label, Tag = action });

            foreach (var (transform, label) in CustomMenuChoices.Transforms)
                CmbItemTransform.Items.Add(new ComboBoxItem { Content = label, Tag = transform });

            _contextAllChip = AddContextChip(Loc.Get("Menus.ContextAll"), null);
            foreach (var (kind, labelKey) in ContextChoices)
                AddContextChip(Loc.Get(labelKey), kind);

            _menuProfiles = new ObservableCollection<CustomMenuProfile>(
                (SettingsService.Current.CustomProfiles ?? new List<CustomMenuProfile>())
                    .Where(p => p is not null)
                    .Select(CloneProfile));
            ListMenuProfiles.ItemsSource = _menuProfiles;
            ListItems.ItemsSource = _menuItems;

            _profileView = CollectionViewSource.GetDefaultView(_menuProfiles);
            _profileView.Filter = ProfileMatchesSearch;
            _itemView = CollectionViewSource.GetDefaultView(_menuItems);
            _itemView.Filter = ItemMatchesSearch;

            if (_menuProfiles.Count > 0)
            {
                ListMenuProfiles.SelectedIndex = 0;
                LoadProfileEditor(_menuProfiles[0]);   // o SelectionChanged acima é ignorado pelo guarda de carga
            }
            else
            {
                LoadProfileEditor(null);
            }
        }
        finally
        {
            _loadingMenus = false;
        }

        _menusDirty = false;
        UpdateMenuButtons();
        RefreshPreview();
    }

    private ToggleButton AddContextChip(string label, RadialContextKind? kind)
    {
        var chip = new ToggleButton
        {
            Content = label,
            Tag = kind,
            Style = (Style)FindResource("ContextChip"),
        };
        chip.Checked += ContextChip_Changed;
        chip.Unchecked += ContextChip_Changed;
        AutomationProperties.SetName(chip, Loc.Format("Menus.ContextAutomation", label));
        PnlContexts.Children.Add(chip);
        _contextChips.Add(chip);
        return chip;
    }

    /// <summary>"Todos" é o estado vazio (nenhum contexto salvo); os demais chips são exclusivos dele.</summary>
    private void ContextChip_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingContexts || _loadingMenus || _selectedProfile is null || sender is not ToggleButton chip) return;

        _syncingContexts = true;
        try
        {
            if (chip == _contextAllChip)
            {
                if (chip.IsChecked == true)
                    foreach (ToggleButton other in _contextChips.Where(c => c != chip))
                        other.IsChecked = false;
                else
                    chip.IsChecked = true;   // desmarcar "Todos" sozinho deixaria o menu sem contexto
            }
            else if (chip.IsChecked == true)
            {
                if (_contextAllChip is not null) _contextAllChip.IsChecked = false;
            }
            else if (!_contextChips.Any(c => c != _contextAllChip && c.IsChecked == true))
            {
                if (_contextAllChip is not null) _contextAllChip.IsChecked = true;
            }
        }
        finally
        {
            _syncingContexts = false;
        }

        ProfileField_Changed(sender, e);
    }

    private void ApplyContexts(IReadOnlyCollection<RadialContextKind> contexts)
    {
        _syncingContexts = true;
        try
        {
            bool any = false;
            foreach (ToggleButton chip in _contextChips)
            {
                if (chip == _contextAllChip) continue;

                bool on = chip.Tag is RadialContextKind kind && contexts.Contains(kind);
                chip.IsChecked = on;
                any |= on;
            }

            if (_contextAllChip is not null) _contextAllChip.IsChecked = !any;
        }
        finally
        {
            _syncingContexts = false;
        }
    }

    // ------------------------------------------------------------------
    // Busca nas listas
    // ------------------------------------------------------------------

    private void ProfileSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingMenus) return;

        _profileSearch = (TxtSearchProfiles.Text ?? "").Trim();

        // O filtro tira o perfil selecionado da visão e o ListBox limpa a seleção: o editor
        // continuaria no mesmo rascunho, então a troca de seleção não pode recarregá-lo.
        bool loading = _loadingMenus;
        _loadingMenus = true;
        try
        {
            _profileView?.Refresh();
            RestoreProfileSelection();
        }
        finally
        {
            _loadingMenus = loading;
        }

        UpdateProfileSearchState();
    }

    private void RestoreProfileSelection()
    {
        if (_selectedProfile is null || _profileView?.Contains(_selectedProfile) != true) return;
        if (!ReferenceEquals(ListMenuProfiles.SelectedItem, _selectedProfile))
            ListMenuProfiles.SelectedItem = _selectedProfile;
    }

    private void ItemSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingMenus) return;

        _itemSearch = (TxtSearchItems.Text ?? "").Trim();
        ApplyItemSearch();
    }

    private void ApplyItemSearch()
    {
        _itemView?.Refresh();

        // O item filtrado sai da visão e a seleção cai junto: escolhe o primeiro visível
        // para o editor continuar mostrando algo enquanto a busca estiver ativa.
        if (ListItems.SelectedItem is null && ListItems.Items.Count > 0)
            ListItems.SelectedIndex = 0;

        UpdateItemSearchState();
    }

    /// <summary>Ao trocar de menu a busca de itens sai: o texto antigo esconderia a lista nova.</summary>
    private void ResetItemSearch()
    {
        if (TxtSearchItems is null) return;

        _itemSearch = "";
        if ((TxtSearchItems.Text ?? "").Length > 0)
        {
            bool loading = _loadingMenus;
            _loadingMenus = true;
            try { TxtSearchItems.Text = ""; }
            finally { _loadingMenus = loading; }
        }

        _itemView?.Refresh();
        UpdateItemSearchState();
    }

    private bool ProfileMatchesSearch(object candidate)
    {
        if (_profileSearch.Length == 0) return true;
        if (candidate is not CustomMenuProfile profile) return false;

        return Contains(profile.Name)
            || Contains(profile.ProcessName)
            || Contains(profile.TitleContains)
            || (profile.Extensions?.Any(Contains) ?? false);

        bool Contains(string? text) =>
            (text ?? "").Contains(_profileSearch, StringComparison.CurrentCultureIgnoreCase);
    }

    private bool ItemMatchesSearch(object candidate)
    {
        if (_itemSearch.Length == 0) return true;
        if (candidate is not CustomMenuItem item) return false;

        return Contains(item.Label)
            || Contains(item.Description)
            || Contains(CustomMenuChoices.KindLabel(item.Kind));

        bool Contains(string? text) =>
            (text ?? "").Contains(_itemSearch, StringComparison.CurrentCultureIgnoreCase);
    }

    private void UpdateProfileSearchState()
    {
        if (PnlNoProfileMatches is null) return;

        bool anyVisible = _profileView is not null && _profileView.Cast<object>().Any();
        PnlNoProfileMatches.Visibility = _profileSearch.Length > 0 && _menuProfiles.Count > 0 && !anyVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateItemSearchState()
    {
        if (PnlNoItemMatches is null) return;

        PnlNoItemMatches.Visibility = _itemSearch.Length > 0 && _menuItems.Count > 0 && ListItems.Items.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>Rebinda o rail só quando algum perfil ganhou ou perdeu itens com aviso.</summary>
    private void RefreshProfileIssueBadges()
    {
        if (_profileView is null) return;

        string signature = string.Join(",", _menuProfiles.Select(p => CustomMenuIssues.CountEnabled(p)));
        if (signature == _profileIssueSignature) return;
        _profileIssueSignature = signature;

        bool loading = _loadingMenus;
        _loadingMenus = true;
        try
        {
            _profileView.Refresh();
            if (_selectedProfile is not null) ListMenuProfiles.SelectedItem = _selectedProfile;
        }
        finally
        {
            _loadingMenus = loading;
        }
    }

    // ------------------------------------------------------------------
    // Perfis
    // ------------------------------------------------------------------

    private void ListMenuProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingMenus) return;

        _loadingMenus = true;
        try
        {
            LoadProfileEditor(ListMenuProfiles.SelectedItem as CustomMenuProfile);
        }
        finally
        {
            _loadingMenus = false;
        }

        UpdateMenuButtons();
        RefreshPreview();
    }

    private void LoadProfileEditor(CustomMenuProfile? profile)
    {
        ResetItemSearch();
        _selectedProfile = profile;
        bool hasProfile = profile is not null;
        PnlProfileEditor.IsEnabled = hasProfile;
        PnlProfileEditor.Visibility = hasProfile ? Visibility.Visible : Visibility.Collapsed;
        PnlNoProfileSelected.Visibility = hasProfile ? Visibility.Collapsed : Visibility.Visible;

        if (profile is null)
        {
            TxtProfileName.Text = "";
            ChkProfileEnabled.IsChecked = false;
            TxtProfileProcess.Text = "";
            TxtProfileTitle.Text = "";
            TxtProfileExtensions.Text = "";
            TxtProfileWindowClass.Text = "";
            TxtProfileHotkey.Text = "";
            SliderProfilePriority.Value = 0;
            TxtPriorityValue.Text = "0";
            ApplyContexts(Array.Empty<RadialContextKind>());
            _selectedItem = null;
            _menuItems.Clear();
            LoadItemEditor(null);
            return;
        }

        TxtProfileName.Text = profile.Name ?? "";
        ChkProfileEnabled.IsChecked = profile.Enabled;
        TxtProfileProcess.Text = profile.ProcessName ?? "";
        TxtProfileTitle.Text = profile.TitleContains ?? "";
        TxtProfileExtensions.Text = string.Join(", ", profile.Extensions ?? new List<string>());
        TxtProfileWindowClass.Text = profile.WindowClass ?? "";
        TxtProfileHotkey.Text = profile.Hotkey ?? "";
        SliderProfilePriority.Value = Math.Clamp(profile.Priority, -100, 100);
        TxtPriorityValue.Text = ((int)SliderProfilePriority.Value).ToString(CultureInfo.InvariantCulture);

        ApplyContexts(profile.Contexts ?? new List<RadialContextKind>());

        _menuItems.Clear();
        foreach (CustomMenuItem item in profile.Items ?? new List<CustomMenuItem>())
            if (item is not null) _menuItems.Add(item);

        _selectedItem = _menuItems.FirstOrDefault();
        ListItems.SelectedItem = _selectedItem;
        LoadItemEditor(_selectedItem);
        UpdateSubmenuSummary();
    }

    private void BtnAddMenuProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_menuProfiles.Count >= CustomMenuNormalizer.MaxProfiles)
        {
            Dialogs.Message(this, Loc.Get("Menus.PageTitle"),
                Loc.Format("Menus.MaxProfiles", CustomMenuNormalizer.MaxProfiles), DialogIcon.Warning);
            return;
        }

        var profile = new CustomMenuProfile
        {
            Name = UniqueName(_menuProfiles.Select(p => p.Name), Loc.Get("Menus.NewMenu")),
            Enabled = true,
        };

        _menuProfiles.Add(profile);
        ListMenuProfiles.SelectedItem = profile;
        MarkMenusDirty();
        FocusAfterProfileChange();
    }

    private void BtnDuplicateMenuProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null) return;

        if (_menuProfiles.Count >= CustomMenuNormalizer.MaxProfiles)
        {
            Dialogs.Message(this, Loc.Get("Menus.PageTitle"),
                Loc.Format("Menus.MaxProfiles", CustomMenuNormalizer.MaxProfiles), DialogIcon.Warning);
            return;
        }

        CustomMenuProfile copy = CloneProfile(_selectedProfile);
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = UniqueName(_menuProfiles.Select(p => p.Name), Loc.Format("Menus.CopySuffix", _selectedProfile.Name));
        copy.Hotkey = "";   // dois menus com o mesmo atalho não podem ser salvos; a cópia começa sem
        foreach (CustomMenuItem item in copy.Items)
            item.Id = Guid.NewGuid().ToString("N");   // itens com o mesmo Id seriam deduplicados entre os perfis

        int index = _menuProfiles.IndexOf(_selectedProfile) + 1;
        _menuProfiles.Insert(index, copy);
        ListMenuProfiles.SelectedItem = copy;
        MarkMenusDirty();
        FocusAfterProfileChange();   // a cópia mantém o nome de origem; o foco já deixa renomear
    }

    private void BtnRemoveMenuProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null) return;

        int index = _menuProfiles.IndexOf(_selectedProfile);
        _menuProfiles.Remove(_selectedProfile);

        if (_menuProfiles.Count > 0)
            ListMenuProfiles.SelectedIndex = Math.Min(index, _menuProfiles.Count - 1);

        MarkMenusDirty();
        FocusAfterProfileChange();
    }

    /// <summary>O painel muda de visibilidade no mesmo turno; o foco entra depois do layout.</summary>
    private void FocusAfterProfileChange()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_menuProfiles.Count == 0)
            {
                BtnAddMenuProfile.Focus();
                return;
            }

            TxtProfileName.Focus();
            TxtProfileName.SelectAll();
        }));
    }

    // ------------------------------------------------------------------
    // Condições e prévia (camadas sobre a aba)
    // ------------------------------------------------------------------

    private sealed record ConditionSnapshot(
        string ProcessName,
        string TitleContains,
        string Extensions,
        string WindowClass,
        string Hotkey,
        double Priority,
        IReadOnlyList<RadialContextKind> Contexts);

    private ConditionSnapshot? _conditionsSnapshot;
    private bool _conditionsDirtyBeforeOpen;

    private void BtnEditConditions_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null) return;

        _conditionsDirtyBeforeOpen = _menusDirty;
        _conditionsSnapshot = CaptureConditions();
        ShowMenuOverlay(ConditionsOverlay);
        TxtProfileProcess.Focus();
    }

    private ConditionSnapshot CaptureConditions() => new(
        TxtProfileProcess.Text ?? "",
        TxtProfileTitle.Text ?? "",
        TxtProfileExtensions.Text ?? "",
        TxtProfileWindowClass.Text ?? "",
        TxtProfileHotkey.Text ?? "",
        SliderProfilePriority.Value,
        (_selectedProfile?.Contexts ?? new List<RadialContextKind>()).ToList());

    /// <summary>Cancelar devolve campos e perfil ao estado da abertura, sem deixar o rascunho sujo.</summary>
    private void RestoreConditions(ConditionSnapshot snapshot)
    {
        if (_selectedProfile is null) return;

        _loadingMenus = true;
        try
        {
            TxtProfileProcess.Text = snapshot.ProcessName;
            TxtProfileTitle.Text = snapshot.TitleContains;
            TxtProfileExtensions.Text = snapshot.Extensions;
            TxtProfileWindowClass.Text = snapshot.WindowClass;
            TxtProfileHotkey.Text = snapshot.Hotkey;
            SliderProfilePriority.Value = snapshot.Priority;
            ApplyContexts(snapshot.Contexts);
        }
        finally
        {
            _loadingMenus = false;
        }

        ApplyProfileFields();
        _menusDirty = _conditionsDirtyBeforeOpen;
        RefreshPreview();
        UpdateMenuButtons();
        UpdateDirtyIndicator();
    }

    private void BtnCancelConditions_Click(object sender, RoutedEventArgs e) => HideMenuOverlays();

    private void BtnDoneConditions_Click(object sender, RoutedEventArgs e) => HideMenuOverlays(restoreConditions: false);

    private void BtnShowWheelPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null) return;

        RefreshPreview();
        ShowMenuOverlay(MenuPreviewOverlay);
        BtnCloseMenuPreview.Focus();
    }

    private void BtnCloseMenuPreview_Click(object sender, RoutedEventArgs e) => HideMenuOverlays();

    private void MenuPreviewScrim_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (MenuPreviewCard.IsMouseOver) return;
        HideMenuOverlays();
    }

    private void ShowMenuOverlay(FrameworkElement overlay)
    {
        ConditionsOverlay.Visibility = Visibility.Collapsed;
        MenuPreviewOverlay.Visibility = Visibility.Collapsed;
        MenusWorkspace.IsEnabled = false;
        overlay.Visibility = Visibility.Visible;
    }

    /// <summary>Fecha o que estiver aberto; devolve true quando havia algo para fechar (Esc).</summary>
    private bool HideMenuOverlays(bool restoreConditions = true)
    {
        bool conditions = ConditionsOverlay.Visibility == Visibility.Visible;
        bool preview = MenuPreviewOverlay.Visibility == Visibility.Visible;
        if (!conditions && !preview) return false;

        if (conditions && restoreConditions && _conditionsSnapshot is { } snapshot)
            RestoreConditions(snapshot);

        _conditionsSnapshot = null;
        ConditionsOverlay.Visibility = Visibility.Collapsed;
        MenuPreviewOverlay.Visibility = Visibility.Collapsed;
        MenusWorkspace.IsEnabled = true;

        FrameworkElement target = conditions ? BtnEditConditions : BtnShowWheelPreview;
        if (target.IsVisible) target.Focus();
        return true;
    }

    // ------------------------------------------------------------------
    // Importar/exportar
    // ------------------------------------------------------------------

    private void BtnImportMenus_Click(object sender, RoutedEventArgs e)
    {
        string? path = ChooseImportPathForTests is { } choose ? choose() : ShowImportDialog();
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!CustomMenuTransfer.TryImportFromFile(
                path,
                _menuProfiles.Select(p => (string?)p.Name).ToList(),
                _menuProfiles.Count,
                out List<CustomMenuProfile> imported,
                out string? reason))
        {
            AppLog.Warning("Menus", $"Importação recusada: {reason}");
            Dialogs.Message(this, Loc.Get("Menus.ImportTitle"),
                Loc.Format("Menus.ImportFailed", reason), DialogIcon.Warning);
            return;
        }

        // O arquivo passa pela mesma validação de alvo do editor antes de entrar
        // no rascunho: um item inválido recusa a importação inteira.
        string? invalid = ValidateProfiles(imported);
        if (invalid is not null)
        {
            AppLog.Warning("Menus", $"Importação recusada: {invalid}");
            Dialogs.Message(this, Loc.Get("Menus.ImportTitle"),
                Loc.Format("Menus.ImportFailed", invalid), DialogIcon.Warning);
            return;
        }

        foreach (CustomMenuProfile profile in imported)
            _menuProfiles.Add(profile);

        MarkMenusDirty();
        ListMenuProfiles.SelectedItem = imported[0];
        ListMenuProfiles.ScrollIntoView(imported[0]);

        int items = imported.Sum(p => p.Items.Count);
        AppLog.Info("Menus", $"{imported.Count} menu(s) importado(s) de '{path}'.");
        Dialogs.Message(this, Loc.Get("Menus.ImportedTitle"),
            Loc.Format("Menus.ImportedMessage",
                DescribeCount(imported.Count, "Common.MenusCountOne", "Common.MenusCountMany"),
                DescribeCount(items, "Common.ItemsCountOne", "Common.ItemsCountMany")),
            DialogIcon.Info);

        FocusAfterProfileChange();
    }

    private void BtnExportMenus_Click(object sender, RoutedEventArgs e)
    {
        if (_menuProfiles.Count == 0) return;

        string? invalid = ValidateMenus();
        if (invalid is not null)
        {
            Dialogs.Message(this, Loc.Get("Menus.PageTitle"), invalid, DialogIcon.Warning);
            return;
        }

        string? path = ChooseExportPathForTests is { } choose
            ? choose()
            : ShowExportDialog("radialflow-menus.json");
        if (string.IsNullOrWhiteSpace(path)) return;

        List<CustomMenuProfile> snapshot = _menuProfiles.ToList();
        if (!CustomMenuTransfer.TryExportToFile(path, snapshot, out string? reason))
        {
            AppLog.Warning("Menus", $"Exportação falhou: {reason}");
            Dialogs.Message(this, Loc.Get("Menus.ExportTitle"),
                reason ?? Loc.Get("Menus.ExportFailed"), DialogIcon.Error);
            return;
        }

        int items = snapshot.Sum(p => p.Items.Count);
        AppLog.Info("Menus", $"{snapshot.Count} menu(s) exportado(s) para '{path}'.");
        Dialogs.Message(this, Loc.Get("Menus.ExportedTitle"),
            Loc.Format("Menus.ExportedMessage",
                DescribeCount(snapshot.Count, "Common.MenusCountOne", "Common.MenusCountMany"),
                DescribeCount(items, "Common.ItemsCountOne", "Common.ItemsCountMany"),
                path),
            DialogIcon.Info);
    }

    private void BtnExportMenuProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null) return;

        // Só este menu: o restante do rascunho pode estar incompleto sem impedir a exportação.
        SyncProfileItems();
        string? invalid = ValidateProfiles(new[] { _selectedProfile });
        if (invalid is not null)
        {
            Dialogs.Message(this, Loc.Get("Menus.PageTitle"), invalid, DialogIcon.Warning);
            return;
        }

        string? path = ChooseExportProfilePathForTests is { } choose
            ? choose()
            : ShowExportDialog(SafeFileName(_selectedProfile.Name) + ".json");
        if (string.IsNullOrWhiteSpace(path)) return;

        CustomMenuProfile snapshot = CloneProfile(_selectedProfile);
        if (!CustomMenuTransfer.TryExportToFile(path, new[] { snapshot }, out string? reason))
        {
            AppLog.Warning("Menus", $"Exportação do menu falhou: {reason}");
            Dialogs.Message(this, Loc.Get("Menus.ExportTitle"),
                reason ?? Loc.Get("Menus.ExportFailed"), DialogIcon.Error);
            return;
        }

        AppLog.Info("Menus", $"Menu '{snapshot.Name}' exportado para '{path}'.");
        Dialogs.Message(this, Loc.Get("Menus.ExportedTitle"),
            Loc.Format("Menus.ExportedMessage",
                DescribeCount(1, "Common.MenusCountOne", "Common.MenusCountMany"),
                DescribeCount(snapshot.Items.Count, "Common.ItemsCountOne", "Common.ItemsCountMany"),
                path),
            DialogIcon.Info);
    }

    private static string SafeFileName(string? name)
    {
        string value = (name ?? "").Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '-');

        return value.Length == 0 ? "radialflow-menu" : value;
    }

    private string? ShowImportDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.Get("Menus.ImportDialogTitle"),
            Filter = Loc.Get("Menus.ImportFilter"),
            CheckFileExists = true,
            Multiselect = false,
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private string? ShowExportDialog(string fileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = Loc.Get("Menus.ExportDialogTitle"),
            Filter = Loc.Get("Menus.ExportFilter"),
            DefaultExt = ".json",
            AddExtension = true,
            FileName = fileName,
            OverwritePrompt = true,
            CheckPathExists = true,
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void ProfileField_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingMenus || _selectedProfile is null) return;

        ApplyProfileFields();
        MarkMenusDirty(debouncePreview: sender is TextBox);
    }

    /// <summary>Leva os campos da aba para o perfil selecionado, sem mexer no estado de rascunho.</summary>
    private void ApplyProfileFields()
    {
        if (_selectedProfile is null) return;

        _selectedProfile.Name = TxtProfileName.Text;
        _selectedProfile.Enabled = ChkProfileEnabled.IsChecked == true;
        _selectedProfile.ProcessName = (TxtProfileProcess.Text ?? "").Trim();
        _selectedProfile.TitleContains = (TxtProfileTitle.Text ?? "").Trim();
        _selectedProfile.Extensions = CustomMenuNormalizer.ParseExtensions(TxtProfileExtensions.Text);
        _selectedProfile.WindowClass = (TxtProfileWindowClass.Text ?? "").Trim();
        _selectedProfile.Hotkey = (TxtProfileHotkey.Text ?? "").Trim();
        _selectedProfile.Priority = (int)Math.Round(SliderProfilePriority.Value);
        TxtPriorityValue.Text = _selectedProfile.Priority.ToString(CultureInfo.InvariantCulture);
        _selectedProfile.Contexts = _contextChips
            .Where(c => c.IsChecked == true && c.Tag is RadialContextKind)
            .Select(c => (RadialContextKind)c.Tag!)
            .ToList();
    }

    private void TxtProfileName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loadingMenus) return;
        RefreshProfileListSelection();
    }

    /// <summary>Ao sair do campo, mostra a forma canônica ("ctrl+alt+1" → "Ctrl+Alt+1").</summary>
    private void TxtProfileHotkey_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loadingMenus) return;

        string raw = (TxtProfileHotkey.Text ?? "").Trim();
        if (raw.Length == 0) return;

        string normalized = HotkeyGesture.Normalize(raw);
        if (normalized.Length == 0 || normalized == TxtProfileHotkey.Text) return;

        // A troca é só de forma (o valor salvo seria o mesmo): não pode marcar o rascunho como alterado.
        bool loading = _loadingMenus;
        _loadingMenus = true;
        try
        {
            TxtProfileHotkey.Text = normalized;
            TxtProfileHotkey.CaretIndex = normalized.Length;
        }
        finally
        {
            _loadingMenus = loading;
        }
    }

    // ------------------------------------------------------------------
    // Itens
    // ------------------------------------------------------------------

    private void ListItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingMenus) return;

        _loadingMenus = true;
        try
        {
            _selectedItem = ListItems.SelectedItem as CustomMenuItem;
            LoadItemEditor(_selectedItem);
        }
        finally
        {
            _loadingMenus = false;
        }

        UpdateMenuButtons();
    }

    private void LoadItemEditor(CustomMenuItem? item)
    {
        PnlItemEditor.IsEnabled = item is not null;
        PnlItemEditor.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        PnlNoItemSelected.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;

        if (item is null)
        {
            TxtItemLabel.Text = "";
            TxtItemDescription.Text = "";
            ChkItemEnabled.IsChecked = false;
            CmbItemGlyph.SelectedItem = null;
            CmbItemKind.SelectedIndex = -1;
            CmbItemAction.SelectedIndex = -1;
            CmbItemTransform.SelectedIndex = -1;
            TxtItemLaunch.Text = "";
            TxtItemUrl.Text = "";
            TxtItemPath.Text = "";
            TxtItemHotkey.Text = "";
            TxtItemText.Text = "";
            SetKindPanels(CustomMenuItemKind.Action);
            UpdateSubmenuSummary();
            UpdateRoutineSummary();
            UpdateMenuButtons();
            return;
        }

        TxtItemLabel.Text = item.Label ?? "";
        TxtItemDescription.Text = item.Description ?? "";
        ChkItemEnabled.IsChecked = item.Enabled;
        SelectGlyph(item.Glyph);
        SelectKind(item.Kind);
        SelectAction(item.Action);
        SelectTransform(item.Transform);
        TxtItemLaunch.Text = item.Kind == CustomMenuItemKind.Launch ? item.Target ?? "" : "";
        TxtItemUrl.Text = item.Kind == CustomMenuItemKind.OpenUrl ? item.Target ?? "" : "";
        TxtItemPath.Text = item.Kind == CustomMenuItemKind.OpenPath ? item.Target ?? "" : "";
        TxtItemHotkey.Text = item.Kind == CustomMenuItemKind.Hotkey ? item.Target ?? "" : "";
        TxtItemText.Text = item.Kind == CustomMenuItemKind.Text ? item.Text ?? "" : "";
        SetKindPanels(item.Kind);
        UpdateSubmenuSummary();
        UpdateRoutineSummary();
        UpdateMenuButtons();
    }

    private void BtnAddMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null) return;

        if (_selectedProfile.Items.Count >= CustomMenuNormalizer.MaxItems)
        {
            Dialogs.Message(this, Loc.Get("Menus.ItemsTitle"),
                Loc.Format("Menus.MaxItems", CustomMenuNormalizer.MaxItems), DialogIcon.Warning);
            return;
        }

        var item = new CustomMenuItem
        {
            Label = UniqueName(_selectedProfile.Items.Select(i => i.Label), Loc.Get("Menus.NewItem")),
            Kind = CustomMenuItemKind.Action,
            Action = RadialAction.Convert,
        };

        _menuItems.Add(item);
        SyncProfileItems();
        ListItems.SelectedItem = item;
        MarkMenusDirty();
    }

    private void BtnRemoveMenuItem_Click(object sender, RoutedEventArgs e)
    {
        int index = ListItems.SelectedIndex;
        if (_selectedProfile is null || index < 0 || index >= _menuItems.Count) return;

        _menuItems.RemoveAt(index);
        SyncProfileItems();

        if (_menuItems.Count > 0)
            ListItems.SelectedIndex = Math.Min(index, _menuItems.Count - 1);

        MarkMenusDirty();
    }

    private void BtnDuplicateMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null || _selectedItem is null) return;

        if (_menuItems.Count >= CustomMenuNormalizer.MaxItems)
        {
            Dialogs.Message(this, Loc.Get("Menus.ItemsTitle"),
                Loc.Format("Menus.MaxItems", CustomMenuNormalizer.MaxItems), DialogIcon.Warning);
            return;
        }

        CustomMenuItem copy = CloneItem(_selectedItem);
        RegenerateIds(copy);
        copy.Label = UniqueName(_menuItems.Select(i => i.Label), CopyLabel(_selectedItem.Label));

        int index = _menuItems.IndexOf(_selectedItem);
        _menuItems.Insert(index + 1, copy);
        SyncProfileItems();
        ListItems.SelectedItem = copy;

        MarkMenusDirty();
    }

    private static void RegenerateIds(CustomMenuItem item)
    {
        item.Id = Guid.NewGuid().ToString("N");
        foreach (CustomMenuItem option in item.Options ?? new List<CustomMenuItem>())
            RegenerateIds(option);
    }

    /// <summary>Rótulo da cópia já truncado: Salvar recusaria um rótulo acima do limite.</summary>
    private static string CopyLabel(string? label)
    {
        string suffix = Loc.Format("Menus.CopySuffix", "");
        string baseLabel = (label ?? "").Trim();

        // Duplicar uma cópia não empilha "(cópia) (cópia)"; o UniqueName numera se o nome já existir.
        if (baseLabel.EndsWith(suffix, StringComparison.Ordinal))
            return baseLabel.Length > CustomMenuNormalizer.MaxLabelLength
                ? baseLabel[..CustomMenuNormalizer.MaxLabelLength]
                : baseLabel;

        int maxBase = Math.Max(1, CustomMenuNormalizer.MaxLabelLength - suffix.Length);
        if (baseLabel.Length > maxBase) baseLabel = baseLabel[..maxBase];

        return baseLabel + suffix;
    }

    private void BtnMoveItemUp_Click(object sender, RoutedEventArgs e) => MoveSelectedItem(-1);

    private void BtnMoveItemDown_Click(object sender, RoutedEventArgs e) => MoveSelectedItem(+1);

    private void MoveSelectedItem(int delta)
    {
        int index = ListItems.SelectedIndex;
        if (index < 0 || index >= _menuItems.Count) return;

        MoveItemToIndex(_menuItems[index], index + delta);
    }

    /// <summary>Move o item para a posição pedida (usado pelos botões, pelo arrasto e pelo teclado).</summary>
    internal void MoveItemToIndex(CustomMenuItem item, int targetIndex)
    {
        int index = _menuItems.IndexOf(item);
        if (index < 0) return;

        targetIndex = Math.Clamp(targetIndex, 0, _menuItems.Count - 1);
        if (targetIndex == index) return;

        _menuItems.Move(index, targetIndex);
        SyncProfileItems();
        ListItems.SelectedItem = item;

        MarkMenusDirty();
    }

    private void ListItems_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleReorderKey(e.Key)) e.Handled = true;
    }

    /// <summary>Alt+↑/↓ reordena; sem Alt as setas continuam sendo navegação da lista.</summary>
    internal bool HandleReorderKey(Key key)
    {
        ModifierKeys modifiers = KeyboardModifiersForTests?.Invoke() ?? Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Alt) == 0 || key is not (Key.Up or Key.Down)) return false;

        MoveSelectedItem(key == Key.Up ? -1 : +1);
        return true;
    }

    private void ListItems_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(ListItems);
        _dragItem = ItemAt(e.OriginalSource as DependencyObject);
    }

    private void ListItems_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is null) return;

        Point position = e.GetPosition(ListItems);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        CustomMenuItem item = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop(ListItems, new DataObject(typeof(CustomMenuItem), item), DragDropEffects.Move);
    }

    private void ListItems_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(CustomMenuItem))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ListItems_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(CustomMenuItem)) is not CustomMenuItem item) return;

        CustomMenuItem? target = ItemAt(e.OriginalSource as DependencyObject);
        MoveItemToIndex(item, target is null ? _menuItems.Count - 1 : _menuItems.IndexOf(target));
        e.Handled = true;
    }

    private CustomMenuItem? ItemAt(DependencyObject? source)
    {
        if (source is null) return null;

        var container = ItemsControl.ContainerFromElement(ListItems, source) as ListBoxItem;
        return container?.DataContext as CustomMenuItem;
    }

    private void CmbItemKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CustomMenuItemKind kind = SelectedKind();
        SetKindPanels(kind);

        if (_loadingMenus || _selectedItem is null) return;

        _selectedItem.Kind = kind;
        _selectedItem.Target = "";
        _selectedItem.Text = "";
        _selectedItem.Steps?.Clear();

        // Os campos do tipo anterior ficam visíveis apenas para ele: limpa os textos para não
        // parecer que o alvo ainda vale depois de trocar o tipo.
        _loadingMenus = true;
        try
        {
            TxtItemLaunch.Text = "";
            TxtItemUrl.Text = "";
            TxtItemPath.Text = "";
            TxtItemHotkey.Text = "";
            TxtItemText.Text = "";
        }
        finally
        {
            _loadingMenus = false;
        }

        UpdateSubmenuSummary();
        UpdateRoutineSummary();
        RefreshItemListRow();
        MarkMenusDirty();
    }

    private void ItemField_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingMenus || _selectedItem is null) return;

        string? issueBefore = CustomMenuIssues.Describe(_selectedItem);
        CustomMenuItemKind kind = SelectedKind();
        _selectedItem.Kind = kind;
        _selectedItem.Label = TxtItemLabel.Text;
        _selectedItem.Description = TxtItemDescription.Text;
        _selectedItem.Enabled = ChkItemEnabled.IsChecked == true;
        _selectedItem.Glyph = (CmbItemGlyph.SelectedItem as GlyphChoice)?.Glyph ?? DefaultGlyph;
        if (CmbItemAction.SelectedItem is ComboBoxItem { Tag: RadialAction action })
            _selectedItem.Action = action;
        if (CmbItemTransform.SelectedItem is ComboBoxItem { Tag: CustomTextTransform transform })
            _selectedItem.Transform = transform;

        _selectedItem.Target = kind switch
        {
            CustomMenuItemKind.Launch => TxtItemLaunch.Text,
            CustomMenuItemKind.OpenUrl => TxtItemUrl.Text,
            CustomMenuItemKind.OpenPath => TxtItemPath.Text,
            CustomMenuItemKind.Hotkey => TxtItemHotkey.Text,
            _ => "",
        };
        _selectedItem.Text = kind == CustomMenuItemKind.Text ? TxtItemText.Text : "";

        // O aviso da linha só precisa de rebind quando ele aparece/desaparece; o texto do rótulo
        // continua sendo atualizado ao sair do campo.
        if (!string.Equals(issueBefore, CustomMenuIssues.Describe(_selectedItem), StringComparison.Ordinal))
            RefreshItemListRow();

        MarkMenusDirty(debouncePreview: sender is TextBox);
    }

    private void TxtItemLabel_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loadingMenus) return;
        RefreshItemListSelection();
    }

    // ------------------------------------------------------------------
    // Submenus
    // ------------------------------------------------------------------

    private void BtnEditSubmenuOptions_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is null) return;

        var initial = (_selectedItem.Options ?? new List<CustomMenuItem>())
            .Where(o => o is not null)
            .Select(CloneItem)
            .ToList();

        var dialog = new SubmenuEditorWindow(_selectedItem.Label ?? "", initial) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _selectedItem.Options = dialog.Options.ToList();
        SyncProfileItems();
        UpdateSubmenuSummary();
        RefreshItemListRow();
        MarkMenusDirty();
    }

    private void UpdateSubmenuSummary()
    {
        bool hasItem = _selectedItem is not null;
        BtnEditSubmenuOptions.IsEnabled = hasItem;

        if (!hasItem)
        {
            TxtSubmenuCount.Text = Loc.Format("Menus.SubmenuCountFormat", 0, CustomMenuNormalizer.MaxOptions);
            TxtSubmenuPreview.Text = Loc.Get("Menus.NoOptionsYet");
            return;
        }

        var options = (_selectedItem!.Options ?? new List<CustomMenuItem>())
            .Where(o => o is not null)
            .ToList();
        var active = CustomMenuNormalizer.ActiveOptions(_selectedItem);

        TxtSubmenuCount.Text = Loc.Format("Menus.SubmenuCountFormat", options.Count, CustomMenuNormalizer.MaxOptions);
        if (options.Count == 0)
        {
            TxtSubmenuPreview.Text = Loc.Get("Menus.NoOptionsYet");
            return;
        }

        if (active.Count == 0)
        {
            TxtSubmenuPreview.Text = Loc.Get("Menus.NoActiveOptions");
            return;
        }

        string labels = string.Join(", ", active.Take(4).Select(o => o.Label));
        if (active.Count > 4) labels += "…";

        TxtSubmenuPreview.Text = active.Count < CustomMenuNormalizer.MinOptions
            ? Loc.Format("Menus.SubmenuActiveOne", active.Count, labels)
            : Loc.Format("Menus.SubmenuActiveMany", active.Count, labels);
    }

    // ------------------------------------------------------------------
    // Rotinas
    // ------------------------------------------------------------------

    private void BtnEditRoutineSteps_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is null) return;

        var initial = (_selectedItem.Steps ?? new List<CustomRoutineStep>())
            .Where(s => s is not null)
            .Select(CloneStep)
            .ToList();

        var dialog = new RoutineEditorWindow(_selectedItem.Label ?? "", initial) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _selectedItem.Steps = dialog.Steps.ToList();
        _selectedItem.RoutineVersion = CustomMenuItem.RoutineSchemaVersion;
        SyncProfileItems();
        UpdateRoutineSummary();
        RefreshItemListRow();
        MarkMenusDirty();
    }

    private void UpdateRoutineSummary()
    {
        bool hasItem = _selectedItem is not null;
        BtnEditRoutineSteps.IsEnabled = hasItem;

        if (!hasItem)
        {
            TxtRoutineCount.Text = Loc.Format("Menus.RoutineCountFormat", 0, CustomRoutineLogic.MaxSteps);
            TxtRoutinePreview.Text = Loc.Get("Menus.NoStepsYet");
            TxtRoutineWarning.Text = "";
            TxtRoutineWarning.Visibility = Visibility.Collapsed;
            return;
        }

        var steps = (_selectedItem!.Steps ?? new List<CustomRoutineStep>())
            .Where(s => s is not null)
            .ToList();

        TxtRoutineCount.Text = Loc.Format("Menus.RoutineCountFormat", steps.Count, CustomRoutineLogic.MaxSteps);
        if (steps.Count == 0)
        {
            TxtRoutinePreview.Text = Loc.Get("Menus.NoStepsYet");
            TxtRoutineWarning.Text = "";
            TxtRoutineWarning.Visibility = Visibility.Collapsed;
            return;
        }

        string description = string.Join(" → ", steps.Take(4).Select(DescribeRoutineStep));
        if (steps.Count > 4) description += " → …";
        TxtRoutinePreview.Text = description;

        if (CustomRoutineLogic.TryBuildPlan(_selectedItem, out _, out string? reason))
        {
            TxtRoutineWarning.Text = "";
            TxtRoutineWarning.Visibility = Visibility.Collapsed;
        }
        else
        {
            TxtRoutineWarning.Text = reason ?? Loc.Get("Menus.RoutineCannotSave");
            TxtRoutineWarning.Visibility = Visibility.Visible;
        }
    }

    private static string DescribeRoutineStep(CustomRoutineStep step) =>
        step.Kind == CustomRoutineStepKind.Delay
            ? $"{step.DelayMilliseconds} ms"
            : CustomMenuChoices.ActionLabel(step.Action);

    private void RefreshItemListRow()
    {
        if (_selectedItem is null) return;

        _loadingMenus = true;
        try
        {
            ListItems.Items.Refresh();
            ListItems.SelectedItem = _selectedItem;
        }
        finally
        {
            _loadingMenus = false;
        }
    }

    // ------------------------------------------------------------------
    // Seletores de alvo
    // ------------------------------------------------------------------

    private void BtnBrowseExecutable_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.Get("Menus.PickExecutable"),
            Filter = Loc.Get("Menus.ExecutableFilter"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true) TxtItemLaunch.Text = dialog.FileName;
    }

    private void BtnBrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.Get("Menus.PickFile"),
            Filter = Loc.Get("Menus.AllFilesFilter"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true) TxtItemPath.Text = dialog.FileName;
    }

    private void BtnBrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.Get("Menus.PickFolder"), Multiselect = false };
        if (dialog.ShowDialog(this) == true) TxtItemPath.Text = dialog.FolderName;
    }

    // ------------------------------------------------------------------
    // Prévia e estado
    // ------------------------------------------------------------------

    private void RefreshPreview()
    {
        if (_previewTimer?.IsEnabled == true) _previewTimer.Stop();

        if (_preview is null) return;

        if (_selectedProfile is null)
        {
            _preview.Show(Array.Empty<CustomMergeEntry>());
            PreviewContext.Text = Loc.Get("Menus.NoProfilePreview");
            PreviewSummary.Text = "";
            PreviewWarning.Text = "";
            PreviewWarning.Visibility = Visibility.Collapsed;
            return;
        }

        RadialContextKind kind = PreviewKind(_selectedProfile);
        IReadOnlyList<RadialSlice> automatic = PreviewAutomatic(kind);
        var items = (_selectedProfile.Items ?? new List<CustomMenuItem>())
            .Where(i => i is not null && i.Enabled)
            .ToList();

        CustomMergePlan plan = CustomMenuResolver.Plan(automatic, items, _ => { });
        _preview.Show(plan.Entries);

        int replacements = plan.Entries.Count(e => e.Status == CustomMergeStatus.Replacement);
        int extras = plan.Entries.Count(e => e.Status == CustomMergeStatus.Extra);

        PreviewContext.Text = Loc.Format("Menus.PreviewContext", ContextLabel(kind)) +
            (_selectedProfile.Enabled ? "." : Loc.Get("Menus.PreviewContextDisabled"));
        string replacementsText = replacements == 1
            ? Loc.Get("Menus.PreviewReplacementsOne")
            : Loc.Format("Menus.PreviewReplacementsMany", replacements);
        string extrasText = extras == 1
            ? Loc.Get("Menus.PreviewExtrasOne")
            : Loc.Format("Menus.PreviewExtrasMany", extras);
        PreviewSummary.Text = Loc.Format("Menus.PreviewSummary", automatic.Count, replacementsText, extrasText);

        // Itens que o Salvar recusaria (alvo vazio, URL sem http/https…): o submenu e a
        // rotina inválidos já foram contados acima.
        int fieldIssues = items.Count(i =>
            !plan.Invalid.Contains(i) && CustomMenuIssues.Describe(i) is not null);

        if (plan.Dropped.Count == 0 && plan.Invalid.Count == 0 && fieldIssues == 0)
        {
            PreviewWarning.Text = "";
            PreviewWarning.Visibility = Visibility.Collapsed;
        }
        else
        {
            var parts = new List<string>();
            int invalidSubmenus = plan.Invalid.Count(i => i.Kind == CustomMenuItemKind.Submenu);
            int invalidRoutines = plan.Invalid.Count(i => i.Kind == CustomMenuItemKind.Routine);

            if (invalidSubmenus == 1)
                parts.Add(Loc.Get("Menus.WarnSubmenuOne"));
            else if (invalidSubmenus > 1)
                parts.Add(Loc.Format("Menus.WarnSubmenus", invalidSubmenus));

            if (invalidRoutines == 1)
                parts.Add(Loc.Get("Menus.WarnRoutineOne"));
            else if (invalidRoutines > 1)
                parts.Add(Loc.Format("Menus.WarnRoutines", invalidRoutines));

            if (fieldIssues == 1)
                parts.Add(Loc.Get("Menus.WarnIssuesOne"));
            else if (fieldIssues > 1)
                parts.Add(Loc.Format("Menus.WarnIssuesMany", fieldIssues));

            if (plan.Dropped.Count == 1)
                parts.Add(Loc.Get("Menus.WarnDroppedOne"));
            else if (plan.Dropped.Count > 1)
                parts.Add(Loc.Format("Menus.WarnDropped", plan.Dropped.Count));

            PreviewWarning.Text = string.Join(" ", parts);
            PreviewWarning.Visibility = Visibility.Visible;
        }
    }

    private static RadialContextKind PreviewKind(CustomMenuProfile profile)
    {
        foreach (RadialContextKind kind in PreviewOrder)
            if ((profile.Contexts ?? new List<RadialContextKind>()).Contains(kind))
                return kind;

        return RadialContextKind.Generic;
    }

    private static IReadOnlyList<RadialSlice> PreviewAutomatic(RadialContextKind kind)
    {
        var cacheKey = (Kind: kind, Language: Loc.Culture.Name);
        if (AutomaticPreviewCache.TryGetValue(cacheKey, out IReadOnlyList<RadialSlice>? cached)) return cached;

        ContextInfo ctx = new(kind, IntPtr.Zero, "", 0, 0,
            kind == RadialContextKind.FileOrFolder ? new[] { @"C:\preview.png" } : ContextInfo.NoPaths);

        // Dependências de mentira: a prévia precisa das MESMAS fatias automáticas, mas não pode tocar em disco.
        var deps = new SliceFactoryDependencies(
            PreviewSliceActions.Instance,
            _ => new[]
            {
                new ConversionOption("pdf", Loc.Format("Menus.PreviewSampleConversionLabel", "PDF"),
                    Loc.Get("Menus.PreviewSampleConversionGroup")),
                new ConversionOption("webp", Loc.Format("Menus.PreviewSampleConversionLabel", "WEBP"),
                    Loc.Get("Menus.PreviewSampleConversionGroup")),
            },
            () => new[]
            {
                new FavoriteAppEntry(Loc.Get("Menus.PreviewSampleApp"), @"C:\app.exe"),
                new FavoriteAppEntry(Loc.Get("Menus.PreviewSampleAppOther"), @"C:\outro.exe"),
            },
            _ => true,
            null);

        IReadOnlyList<RadialSlice> slices = SliceFactory.Build(ctx, deps);
        AutomaticPreviewCache[cacheKey] = slices;
        return slices;
    }

    private static string ContextLabel(RadialContextKind kind) => kind switch
    {
        RadialContextKind.FileOrFolder => Loc.Get("Preview.ContextFileOrFolder"),
        RadialContextKind.DesktopBackground => Loc.Get("Preview.ContextDesktop"),
        RadialContextKind.TitleBar => Loc.Get("Preview.ContextTitleBar"),
        RadialContextKind.TextField => Loc.Get("Preview.ContextTextField"),
        _ => Loc.Get("Preview.ContextGeneric"),
    };

    private void UpdateMenuButtons()
    {
        bool hasProfiles = _menuProfiles.Count > 0;
        bool hasProfile = _selectedProfile is not null;
        MenusWorkspace.Visibility = hasProfiles ? Visibility.Visible : Visibility.Collapsed;
        PnlNoMenus.Visibility = hasProfiles ? Visibility.Collapsed : Visibility.Visible;
        PnlProfileActions.Visibility = hasProfile ? Visibility.Visible : Visibility.Collapsed;

        BtnDuplicateMenuProfile.IsEnabled = hasProfile;
        BtnExportMenuProfile.IsEnabled = hasProfile;
        BtnRemoveMenuProfile.IsEnabled = hasProfile;
        BtnAddMenuItem.IsEnabled = hasProfile;
        BtnExportMenus.IsEnabled = hasProfiles;

        bool hasItem = _selectedItem is not null;
        int index = ListItems.SelectedIndex;
        BtnDuplicateMenuItem.IsEnabled = hasItem;
        BtnRemoveMenuItem.IsEnabled = hasItem;
        BtnMoveItemUp.IsEnabled = hasItem && index > 0;
        BtnMoveItemDown.IsEnabled = hasItem && index >= 0 && index < _menuItems.Count - 1;

        bool hasItems = _menuItems.Count > 0;
        ItemsWorkspace.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        PnlNoItems.Visibility = hasProfile && !hasItems ? Visibility.Visible : Visibility.Collapsed;

        MenuProfileCount.Text = _menuProfiles.Count.ToString(CultureInfo.InvariantCulture);
        MenuItemCount.Text = Loc.Format("Menus.ItemsCountFormat", _menuItems.Count, CustomMenuNormalizer.MaxItems);
        UpdateProfileSummary();
        RefreshProfileIssueBadges();
    }

    /// <summary>Resumo do cabeçalho: as condições que valem agora, sem abrir a seção Aplicação.</summary>
    private void UpdateProfileSummary()
    {
        if (ProfileSummary is null) return;

        if (_selectedProfile is null)
        {
            ProfileSummary.Text = "";
            return;
        }

        var parts = new List<string>();

        string process = (_selectedProfile.ProcessName ?? "").Trim();
        parts.Add(process.Length == 0 ? Loc.Get("Menus.AnyApp") : process);

        var extensions = _selectedProfile.Extensions ?? new List<string>();
        if (extensions.Count > 0)
        {
            string list = string.Join(", ", extensions.Take(3));
            if (extensions.Count > 3) list += "…";
            parts.Add(list);
        }

        string title = (_selectedProfile.TitleContains ?? "").Trim();
        if (title.Length > 0)
            parts.Add(Loc.Format("Menus.SummaryTitleContains", title.Length > 24 ? title[..24] + "…" : title));

        string windowClass = (_selectedProfile.WindowClass ?? "").Trim();
        if (windowClass.Length > 0) parts.Add(windowClass);

        int items = _menuItems.Count;
        parts.Add(items == 1 ? Loc.Get("Menus.SummaryItemsOne") : Loc.Format("Menus.SummaryItemsMany", items));

        string hotkey = HotkeyGesture.Normalize(_selectedProfile.Hotkey ?? "");
        if (hotkey.Length > 0) parts.Add(hotkey);

        ProfileSummary.Text = string.Join("  ·  ", parts);
    }

    private void MarkMenusDirty(bool debouncePreview = false)
    {
        if (_loadingMenus) return;
        _menusDirty = true;

        if (debouncePreview) SchedulePreviewRefresh();
        else RefreshPreview();   // imediato: também cancela um debounce pendente de uma edição anterior

        UpdateMenuButtons();
        UpdateDirtyIndicator();
    }

    /// <summary>Adia a prévia para o fim da digitação: cada tecla não reconstrói a roda inteira.</summary>
    private void SchedulePreviewRefresh()
    {
        if (_previewTimer is null)
        {
            _previewTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(PreviewDebounceMilliseconds),
            };
            _previewTimer.Tick += (_, _) => RefreshPreview();
        }

        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void SyncProfileItems()
    {
        if (_selectedProfile is not null)
            _selectedProfile.Items = _menuItems.ToList();
    }

    // ------------------------------------------------------------------
    // Salvar/cancelar
    // ------------------------------------------------------------------

    /// <summary>Devolve a primeira mensagem de erro de sintaxe do rascunho (null = tudo certo).</summary>
    private string? ValidateMenus()
    {
        SyncProfileItems();
        return ValidateProfiles(_menuProfiles);
    }

    /// <summary>Mesmas regras para o rascunho e para menus importados.</summary>
    private string? ValidateProfiles(IEnumerable<CustomMenuProfile> profiles)
    {
        var hotkeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (CustomMenuProfile profile in profiles)
        {
            string name = (profile.Name ?? "").Trim();
            if (profile.Enabled && name.Length == 0)
                return Loc.Get("Menus.ValidateNameRequired");
            if (name.Length > CustomMenuNormalizer.MaxNameLength)
                return Loc.Format("Menus.ValidateNameTooLong", CustomMenuNormalizer.MaxNameLength);

            if (!profile.Enabled) continue;

            string? hotkeyError = ValidateProfileHotkey(profile, name, hotkeys);
            if (hotkeyError is not null) return hotkeyError;

            foreach (CustomMenuItem item in profile.Items ?? new List<CustomMenuItem>())
            {
                if (item is null || !item.Enabled) continue;

                string label = (item.Label ?? "").Trim();
                if (label.Length == 0)
                    return Loc.Format("Menus.ValidateItemLabelRequired", name);
                if (label.Length > CustomMenuNormalizer.MaxLabelLength)
                    return Loc.Format("Menus.ValidateLabelTooLong", label, CustomMenuNormalizer.MaxLabelLength);
                if ((item.Description ?? "").Length > CustomMenuNormalizer.MaxDescriptionLength)
                    return Loc.Format("Menus.ValidateDescriptionTooLong", label, CustomMenuNormalizer.MaxDescriptionLength);

                switch (item.Kind)
                {
                    case CustomMenuItemKind.Submenu:
                        string? submenuError = ValidateSubmenu(item, label);
                        if (submenuError is not null) return submenuError;
                        break;

                    default:
                        string? targetError = CustomMenuValidation.ValidateTarget(item, label);
                        if (targetError is not null) return targetError;
                        break;
                }
            }
        }

        return null;
    }

    private string? ValidateProfileHotkey(CustomMenuProfile profile, string name, HashSet<string> hotkeys)
    {
        string raw = (profile.Hotkey ?? "").Trim();
        string hotkey = HotkeyGesture.Normalize(raw);

        if (raw.Length > 0 && hotkey.Length == 0)
            return Loc.Format("Menus.ValidateProfileHotkeyInvalid", name);

        if (hotkey.Length == 0) return null;

        if (string.Equals(hotkey, HotkeyGesture.Normalize(_hotkey), StringComparison.Ordinal))
            return Loc.Format("Menus.ValidateProfileHotkeyGlobal", name, hotkey);

        if (!hotkeys.Add(hotkey))
            return Loc.Format("Menus.ValidateProfileHotkeyDuplicate", name, hotkey);

        if (!CustomMenuHotkeys.HasActiveItem(profile))
            return Loc.Format("Menus.ValidateProfileHotkeyNoItems", name);

        return null;
    }

    private static string? ValidateSubmenu(CustomMenuItem item, string label)
    {
        var options = (item.Options ?? new List<CustomMenuItem>())
            .Where(o => o is not null)
            .ToList();

        if (options.Count > CustomMenuNormalizer.MaxOptions)
            return Loc.Format("Menus.ValidateSubmenuTooMany", label, CustomMenuNormalizer.MaxOptions);

        var active = CustomMenuNormalizer.ActiveOptions(item);
        if (active.Count < CustomMenuNormalizer.MinOptions)
            return Loc.Format("Menus.ValidateSubmenuMinOptions", label, CustomMenuNormalizer.MinOptions);

        foreach (CustomMenuItem option in active)
        {
            string? error = CustomMenuValidation.ValidateOption(option, label);
            if (error is not null) return error;
        }

        return null;
    }

    /// <summary>Copia o rascunho para as configurações, normalizando os campos e SEM compartilhar listas/itens.</summary>
    private void ApplyMenusTo(AppSettings settings)
    {
        SyncProfileItems();

        foreach (CustomMenuProfile profile in _menuProfiles)
        {
            profile.Name = (profile.Name ?? "").Trim();
            profile.ProcessName = CustomMenuNormalizer.NormalizeProcessName(profile.ProcessName);
            profile.TitleContains = (profile.TitleContains ?? "").Trim();
            profile.Extensions = CustomMenuNormalizer.NormalizeExtensions(profile.Extensions);
            profile.WindowClass = CustomMenuNormalizer.NormalizeWindowClass(profile.WindowClass);
            profile.Hotkey = CustomMenuNormalizer.NormalizeHotkey(profile.Hotkey, profile.Name);
            profile.Priority = Math.Clamp(profile.Priority, -100, 100);

            foreach (CustomMenuItem item in profile.Items ?? new List<CustomMenuItem>())
            {
                if (item is null) continue;
                item.Label = (item.Label ?? "").Trim();
                item.Description = (item.Description ?? "").Trim();
                item.Target = (item.Target ?? "").Trim();
                item.Text ??= "";

                if (item.Kind == CustomMenuItemKind.Routine)
                {
                    item.Steps ??= new List<CustomRoutineStep>();
                    item.RoutineVersion = CustomMenuItem.RoutineSchemaVersion;
                }
                else
                {
                    item.Steps?.Clear();
                }

                if (item.Kind != CustomMenuItemKind.TextTransform)
                    item.Transform = CustomTextTransform.Upper;

                foreach (CustomMenuItem option in item.Options ?? new List<CustomMenuItem>())
                {
                    if (option is null) continue;
                    option.Label = (option.Label ?? "").Trim();
                    option.Description = (option.Description ?? "").Trim();
                    option.Target = (option.Target ?? "").Trim();
                    option.Text ??= "";
                }
            }
        }

        settings.CustomProfiles = _menuProfiles.Select(CloneProfile).ToList();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        HideMenuOverlays();   // fechar com diálogo aberto equivale a cancelar as condições

        // A gravação do atalho prende a combinação: precisa terminar mesmo se o fechamento for cancelado.
        if (_recordingHotkey) StopRecordingHotkey();

        // Aplica a última edição pendente antes do aviso; o timer não sobrevive ao fechamento.
        if (_previewTimer?.IsEnabled == true)
        {
            if (_menusDirty) RefreshPreview();
            else _previewTimer.Stop();
        }

        List<string> changed = ChangedSections();
        if (changed.Count > 0)
        {
            bool discard = Dialogs.Confirm(this, Loc.Get("Settings.DiscardTitle"),
                Loc.Format("Settings.DiscardPrompt", string.Join(", ", changed)),
                Loc.Get("Settings.Discard"), Loc.Get("Settings.ContinueEditing"), DialogIcon.Warning, defaultIsConfirm: false);
            if (!discard)
            {
                e.Cancel = true;
                return;
            }
        }

        base.OnClosing(e);
    }

    // ------------------------------------------------------------------
    // Utilitários
    // ------------------------------------------------------------------

    private static CustomMenuProfile CloneProfile(CustomMenuProfile profile) => new()
    {
        Id = profile.Id,
        Name = profile.Name,
        Enabled = profile.Enabled,
        Priority = profile.Priority,
        ProcessName = profile.ProcessName,
        TitleContains = profile.TitleContains,
        Extensions = new List<string>(profile.Extensions ?? new List<string>()),
        WindowClass = profile.WindowClass,
        Hotkey = profile.Hotkey,
        Contexts = new List<RadialContextKind>(profile.Contexts ?? new List<RadialContextKind>()),
        Items = (profile.Items ?? new List<CustomMenuItem>())
            .Where(i => i is not null)
            .Select(CloneItem)
            .ToList(),
    };

    private static CustomMenuItem CloneItem(CustomMenuItem item) => new()
    {
        Id = item.Id,
        Label = item.Label,
        Glyph = item.Glyph,
        Description = item.Description,
        Enabled = item.Enabled,
        Kind = item.Kind,
        Action = item.Action,
        Target = item.Target,
        Text = item.Text,
        Transform = item.Transform,
        Options = (item.Options ?? new List<CustomMenuItem>())
            .Where(o => o is not null)
            .Select(CloneItem)
            .ToList(),
        RoutineVersion = item.RoutineVersion,
        Steps = (item.Steps ?? new List<CustomRoutineStep>())
            .Where(s => s is not null)
            .Select(CloneStep)
            .ToList(),
    };

    private static CustomRoutineStep CloneStep(CustomRoutineStep step) => new()
    {
        Kind = step.Kind,
        Action = step.Action,
        DelayMilliseconds = step.DelayMilliseconds,
    };

    private static string DescribeCount(int value, string oneKey, string manyKey) =>
        value == 1 ? Loc.Get(oneKey) : Loc.Format(manyKey, value);

    private static string UniqueName(IEnumerable<string?> names, string baseName)
    {
        var used = names.Select(n => (n ?? "").Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(baseName)) return baseName;

        for (int i = 2; i < 1000; i++)
        {
            string candidate = $"{baseName} {i}";
            if (!used.Contains(candidate)) return candidate;
        }

        return baseName;
    }

    private void SelectGlyph(string? glyph)
    {
        string value = string.IsNullOrWhiteSpace(glyph) ? DefaultGlyph : glyph;
        GlyphChoice? choice = _glyphChoices.FirstOrDefault(g => g.Glyph == value);
        if (choice is null)
        {
            choice = new GlyphChoice(value, Loc.Get("Menus.CustomGlyph"));
            _glyphChoices.Add(choice);
        }

        CmbItemGlyph.SelectedItem = choice;
    }

    private void SelectKind(CustomMenuItemKind kind)
    {
        foreach (ComboBoxItem entry in CmbItemKind.Items)
        {
            if (entry.Tag is CustomMenuItemKind candidate && candidate == kind)
            {
                CmbItemKind.SelectedItem = entry;
                return;
            }
        }

        CmbItemKind.SelectedIndex = 0;
    }

    private void SelectAction(RadialAction action)
    {
        foreach (ComboBoxItem entry in CmbItemAction.Items)
        {
            if (entry.Tag is RadialAction candidate && candidate == action)
            {
                CmbItemAction.SelectedItem = entry;
                return;
            }
        }

        CmbItemAction.SelectedIndex = 0;
    }

    private void SelectTransform(CustomTextTransform transform)
    {
        foreach (ComboBoxItem entry in CmbItemTransform.Items)
        {
            if (entry.Tag is CustomTextTransform candidate && candidate == transform)
            {
                CmbItemTransform.SelectedItem = entry;
                return;
            }
        }

        CmbItemTransform.SelectedIndex = 0;
    }

    private CustomMenuItemKind SelectedKind() =>
        CmbItemKind.SelectedItem is ComboBoxItem { Tag: CustomMenuItemKind kind } ? kind : CustomMenuItemKind.Action;

    private void SetKindPanels(CustomMenuItemKind kind)
    {
        bool submenu = kind == CustomMenuItemKind.Submenu;
        bool routine = kind == CustomMenuItemKind.Routine;
        BdItemTarget.Visibility = submenu || routine ? Visibility.Collapsed : Visibility.Visible;
        PnlItemSubmenu.Visibility = submenu ? Visibility.Visible : Visibility.Collapsed;
        PnlItemRoutine.Visibility = routine ? Visibility.Visible : Visibility.Collapsed;

        PnlItemAction.Visibility = kind == CustomMenuItemKind.Action ? Visibility.Visible : Visibility.Collapsed;
        PnlItemLaunch.Visibility = kind == CustomMenuItemKind.Launch ? Visibility.Visible : Visibility.Collapsed;
        PnlItemUrl.Visibility = kind == CustomMenuItemKind.OpenUrl ? Visibility.Visible : Visibility.Collapsed;
        PnlItemPath.Visibility = kind == CustomMenuItemKind.OpenPath ? Visibility.Visible : Visibility.Collapsed;
        PnlItemHotkey.Visibility = kind == CustomMenuItemKind.Hotkey ? Visibility.Visible : Visibility.Collapsed;
        PnlItemText.Visibility = kind == CustomMenuItemKind.Text ? Visibility.Visible : Visibility.Collapsed;
        PnlItemTextTransform.Visibility = kind == CustomMenuItemKind.TextTransform ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshProfileListSelection()
    {
        CustomMenuProfile? selected = _selectedProfile;
        _loadingMenus = true;
        try
        {
            ListMenuProfiles.Items.Refresh();
            if (selected is not null) ListMenuProfiles.SelectedItem = selected;
        }
        finally
        {
            _loadingMenus = false;
        }
    }

    private void RefreshItemListSelection()
    {
        CustomMenuItem? selected = _selectedItem;
        int index = ListItems.SelectedIndex;
        _loadingMenus = true;
        try
        {
            ListItems.Items.Refresh();
            if (selected is not null) ListItems.SelectedItem = selected;
            else if (index >= 0) ListItems.SelectedIndex = index;
        }
        finally
        {
            _loadingMenus = false;
        }

        UpdateMenuButtons();
    }

    private sealed class PreviewSliceActions : ISliceActions
    {
        public static readonly PreviewSliceActions Instance = new();

        public void Run(RadialAction action, ContextInfo context) { }
        public void ConvertTo(IReadOnlyList<string> sourceFiles, string targetFormat) { }
        public void LaunchFavorite(FavoriteAppEntry app) { }
        public void RunCustom(CustomMenuItem item, ContextInfo context) { }
    }
}
