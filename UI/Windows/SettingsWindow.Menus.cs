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
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using RadialFlow.Config;
using RadialFlow.Context;
using RadialFlow.Services;

namespace RadialFlow.UI;

/// <summary>Item do seletor de ícones (par de glifo Segoe + nome amigável).</summary>
public sealed record GlyphChoice(string Glyph, string Label);

public partial class SettingsWindow
{
    private const string DefaultGlyph = "\uE734";

    private static readonly (RadialContextKind Kind, string Label)[] ContextChoices =
    [
        (RadialContextKind.FileOrFolder, "Arquivos e pastas"),
        (RadialContextKind.DesktopBackground, "Área de trabalho"),
        (RadialContextKind.TitleBar, "Barra de título"),
        (RadialContextKind.TextField, "Campos de texto"),
        (RadialContextKind.Generic, "Genérico"),
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

    private ObservableCollection<CustomMenuProfile> _menuProfiles = new();
    private readonly ObservableCollection<CustomMenuItem> _menuItems = new();
    private CustomMenuProfile? _selectedProfile;
    private CustomMenuItem? _selectedItem;
    private MenuPreview? _preview;
    private bool _loadingMenus;
    private bool _menusDirty;
    private DispatcherTimer? _previewTimer;

    private const int PreviewDebounceMilliseconds = 120;

    // Dependências fixas (nada lê configurações nem disco): as fatias automáticas de cada contexto não mudam.
    private static readonly Dictionary<RadialContextKind, IReadOnlyList<RadialSlice>> AutomaticPreviewCache = new();

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

            foreach (var (kind, label) in ContextChoices)
            {
                var check = new CheckBox { Content = label, Tag = kind, Margin = new Thickness(0, 4, 12, 4) };
                check.Checked += ProfileField_Changed;
                check.Unchecked += ProfileField_Changed;
                AutomationProperties.SetName(check, $"Contexto: {label}");
                PnlContexts.Children.Add(check);
            }

            _menuProfiles = new ObservableCollection<CustomMenuProfile>(
                (SettingsService.Current.CustomProfiles ?? new List<CustomMenuProfile>())
                    .Where(p => p is not null)
                    .Select(CloneProfile));
            ListMenuProfiles.ItemsSource = _menuProfiles;
            ListItems.ItemsSource = _menuItems;

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
        _selectedProfile = profile;
        bool hasProfile = profile is not null;
        PnlProfileEditor.IsEnabled = hasProfile;

        MenusScroll.Visibility = hasProfile ? Visibility.Visible : Visibility.Collapsed;
        PnlNoProfileSelected.Visibility = hasProfile ? Visibility.Collapsed : Visibility.Visible;

        if (profile is null)
        {
            TxtProfileName.Text = "";
            ChkProfileEnabled.IsChecked = false;
            TxtProfileProcess.Text = "";
            TxtProfileTitle.Text = "";
            SliderProfilePriority.Value = 0;
            TxtPriorityValue.Text = "0";
            foreach (CheckBox check in PnlContexts.Children.OfType<CheckBox>()) check.IsChecked = false;
            _selectedItem = null;
            _menuItems.Clear();
            LoadItemEditor(null);
            return;
        }

        TxtProfileName.Text = profile.Name ?? "";
        ChkProfileEnabled.IsChecked = profile.Enabled;
        TxtProfileProcess.Text = profile.ProcessName ?? "";
        TxtProfileTitle.Text = profile.TitleContains ?? "";
        SliderProfilePriority.Value = Math.Clamp(profile.Priority, -100, 100);
        TxtPriorityValue.Text = ((int)SliderProfilePriority.Value).ToString(CultureInfo.InvariantCulture);

        var contexts = profile.Contexts ?? new List<RadialContextKind>();
        foreach (CheckBox check in PnlContexts.Children.OfType<CheckBox>())
            check.IsChecked = check.Tag is RadialContextKind kind && contexts.Contains(kind);

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
            Dialogs.Message(this, "Menus personalizados",
                $"Você pode ter no máximo {CustomMenuNormalizer.MaxProfiles} menus personalizados.", DialogIcon.Warning);
            return;
        }

        var profile = new CustomMenuProfile
        {
            Name = UniqueName(_menuProfiles.Select(p => p.Name), "Novo menu"),
            Enabled = true,
        };

        _menuProfiles.Add(profile);
        ListMenuProfiles.SelectedItem = profile;
        MarkMenusDirty();
    }

    private void BtnDuplicateMenuProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null) return;

        if (_menuProfiles.Count >= CustomMenuNormalizer.MaxProfiles)
        {
            Dialogs.Message(this, "Menus personalizados",
                $"Você pode ter no máximo {CustomMenuNormalizer.MaxProfiles} menus personalizados.", DialogIcon.Warning);
            return;
        }

        CustomMenuProfile copy = CloneProfile(_selectedProfile);
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = UniqueName(_menuProfiles.Select(p => p.Name), $"{_selectedProfile.Name} (cópia)");
        foreach (CustomMenuItem item in copy.Items)
            item.Id = Guid.NewGuid().ToString("N");   // itens com o mesmo Id seriam deduplicados entre os perfis

        int index = _menuProfiles.IndexOf(_selectedProfile) + 1;
        _menuProfiles.Insert(index, copy);
        ListMenuProfiles.SelectedItem = copy;
        MarkMenusDirty();
    }

    private void BtnRemoveMenuProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null) return;

        int index = _menuProfiles.IndexOf(_selectedProfile);
        _menuProfiles.Remove(_selectedProfile);

        if (_menuProfiles.Count > 0)
            ListMenuProfiles.SelectedIndex = Math.Min(index, _menuProfiles.Count - 1);

        MarkMenusDirty();
    }

    private void ProfileField_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingMenus || _selectedProfile is null) return;

        _selectedProfile.Name = TxtProfileName.Text;
        _selectedProfile.Enabled = ChkProfileEnabled.IsChecked == true;
        _selectedProfile.ProcessName = (TxtProfileProcess.Text ?? "").Trim();
        _selectedProfile.TitleContains = (TxtProfileTitle.Text ?? "").Trim();
        _selectedProfile.Priority = (int)Math.Round(SliderProfilePriority.Value);
        TxtPriorityValue.Text = _selectedProfile.Priority.ToString(CultureInfo.InvariantCulture);
        _selectedProfile.Contexts = PnlContexts.Children.OfType<CheckBox>()
            .Where(c => c.IsChecked == true && c.Tag is RadialContextKind)
            .Select(c => (RadialContextKind)c.Tag)
            .ToList();

        MarkMenusDirty(debouncePreview: sender is TextBox);
    }

    private void TxtProfileName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loadingMenus) return;
        RefreshProfileListSelection();
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
            Dialogs.Message(this, "Itens do menu",
                $"Cada menu pode ter no máximo {CustomMenuNormalizer.MaxItems} itens. " +
                "Para mais opções, crie outro menu com condições diferentes.", DialogIcon.Warning);
            return;
        }

        var item = new CustomMenuItem
        {
            Label = UniqueName(_selectedProfile.Items.Select(i => i.Label), "Novo item"),
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

    private void BtnMoveItemUp_Click(object sender, RoutedEventArgs e) => MoveSelectedItem(-1);

    private void BtnMoveItemDown_Click(object sender, RoutedEventArgs e) => MoveSelectedItem(+1);

    private void MoveSelectedItem(int delta)
    {
        int index = ListItems.SelectedIndex;
        int target = index + delta;
        if (_selectedProfile is null || index < 0 || target < 0 || target >= _menuItems.Count) return;

        CustomMenuItem item = _menuItems[index];
        _menuItems.Move(index, target);
        SyncProfileItems();
        ListItems.SelectedItem = item;

        MarkMenusDirty();
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

        CustomMenuItemKind kind = SelectedKind();
        _selectedItem.Kind = kind;
        _selectedItem.Label = TxtItemLabel.Text;
        _selectedItem.Description = TxtItemDescription.Text;
        _selectedItem.Enabled = ChkItemEnabled.IsChecked == true;
        _selectedItem.Glyph = (CmbItemGlyph.SelectedItem as GlyphChoice)?.Glyph ?? DefaultGlyph;
        if (CmbItemAction.SelectedItem is ComboBoxItem { Tag: RadialAction action })
            _selectedItem.Action = action;

        _selectedItem.Target = kind switch
        {
            CustomMenuItemKind.Launch => TxtItemLaunch.Text,
            CustomMenuItemKind.OpenUrl => TxtItemUrl.Text,
            CustomMenuItemKind.OpenPath => TxtItemPath.Text,
            CustomMenuItemKind.Hotkey => TxtItemHotkey.Text,
            _ => "",
        };
        _selectedItem.Text = kind == CustomMenuItemKind.Text ? TxtItemText.Text : "";

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
            TxtSubmenuCount.Text = $"0 de {CustomMenuNormalizer.MaxOptions} opções";
            TxtSubmenuPreview.Text = "Nenhuma opção ainda.";
            return;
        }

        var options = (_selectedItem!.Options ?? new List<CustomMenuItem>())
            .Where(o => o is not null)
            .ToList();
        var active = CustomMenuNormalizer.ActiveOptions(_selectedItem);

        TxtSubmenuCount.Text = $"{options.Count} de {CustomMenuNormalizer.MaxOptions} opções";
        if (options.Count == 0)
        {
            TxtSubmenuPreview.Text = "Nenhuma opção ainda.";
            return;
        }

        if (active.Count == 0)
        {
            TxtSubmenuPreview.Text = "Nenhuma opção ativa.";
            return;
        }

        string labels = string.Join(", ", active.Take(4).Select(o => o.Label));
        if (active.Count > 4) labels += "…";

        TxtSubmenuPreview.Text = active.Count < CustomMenuNormalizer.MinOptions
            ? $"{active.Count} ativa: {labels}. Adicione mais uma para abrir o segundo anel."
            : $"{active.Count} ativas: {labels}.";
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
            TxtRoutineCount.Text = $"0 de {CustomRoutineLogic.MaxSteps} passos";
            TxtRoutinePreview.Text = "Nenhum passo ainda.";
            TxtRoutineWarning.Text = "";
            TxtRoutineWarning.Visibility = Visibility.Collapsed;
            return;
        }

        var steps = (_selectedItem!.Steps ?? new List<CustomRoutineStep>())
            .Where(s => s is not null)
            .ToList();

        TxtRoutineCount.Text = $"{steps.Count} de {CustomRoutineLogic.MaxSteps} passos";
        if (steps.Count == 0)
        {
            TxtRoutinePreview.Text = "Nenhum passo ainda.";
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
            TxtRoutineWarning.Text = reason ?? "A rotina ainda não pode ser salva.";
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
            Title = "Selecionar executável",
            Filter = "Executáveis (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true) TxtItemLaunch.Text = dialog.FileName;
    }

    private void BtnBrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selecionar arquivo",
            Filter = "Todos os arquivos (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true) TxtItemPath.Text = dialog.FileName;
    }

    private void BtnBrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Selecionar pasta", Multiselect = false };
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
            PreviewContext.Text = "Nenhum menu selecionado.";
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

        PreviewContext.Text = $"Prévia no contexto “{ContextLabel(kind)}”" +
            (_selectedProfile.Enabled ? "." : " (este menu está desativado).");
        string replacementsText = replacements == 1 ? "1 substituição" : $"{replacements} substituições";
        string extrasText = extras == 1 ? "1 extra" : $"{extras} extras";
        PreviewSummary.Text = $"{automatic.Count} fatias automáticas · {replacementsText} · {extrasText}.";

        if (plan.Dropped.Count == 0 && plan.Invalid.Count == 0)
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
                parts.Add("1 submenu precisa de 2 opções ativas e não aparece.");
            else if (invalidSubmenus > 1)
                parts.Add($"{invalidSubmenus} submenus precisam de 2 opções ativas e não aparecem.");

            if (invalidRoutines == 1)
                parts.Add("1 rotina inválida não aparece.");
            else if (invalidRoutines > 1)
                parts.Add($"{invalidRoutines} rotinas inválidas não aparecem.");

            if (plan.Dropped.Count == 1)
                parts.Add("1 item será ignorado (limite de 8 fatias).");
            else if (plan.Dropped.Count > 1)
                parts.Add($"{plan.Dropped.Count} itens serão ignorados (limite de 8 fatias).");

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
        if (AutomaticPreviewCache.TryGetValue(kind, out IReadOnlyList<RadialSlice>? cached)) return cached;

        ContextInfo ctx = new(kind, IntPtr.Zero, "", 0, 0,
            kind == RadialContextKind.FileOrFolder ? new[] { @"C:\preview.png" } : ContextInfo.NoPaths);

        // Dependências de mentira: a prévia precisa das MESMAS fatias automáticas, mas não pode tocar em disco.
        var deps = new SliceFactoryDependencies(
            PreviewSliceActions.Instance,
            _ => new[]
            {
                new ConversionOption("pdf", "PDF — prévia", "Prévia"),
                new ConversionOption("webp", "WEBP — prévia", "Prévia"),
            },
            () => new[]
            {
                new FavoriteAppEntry("App de exemplo", @"C:\app.exe"),
                new FavoriteAppEntry("Outro app", @"C:\outro.exe"),
            },
            _ => true,
            null);

        IReadOnlyList<RadialSlice> slices = SliceFactory.Build(ctx, deps);
        AutomaticPreviewCache[kind] = slices;
        return slices;
    }

    private static string ContextLabel(RadialContextKind kind) => kind switch
    {
        RadialContextKind.FileOrFolder => "arquivos e pastas",
        RadialContextKind.DesktopBackground => "área de trabalho",
        RadialContextKind.TitleBar => "barra de título",
        RadialContextKind.TextField => "campos de texto",
        _ => "genérico",
    };

    private void UpdateMenuButtons()
    {
        bool hasProfiles = _menuProfiles.Count > 0;
        bool hasProfile = _selectedProfile is not null;
        MenusWorkspace.Visibility = hasProfiles ? Visibility.Visible : Visibility.Collapsed;
        PnlNoMenus.Visibility = hasProfiles ? Visibility.Collapsed : Visibility.Visible;
        PnlProfileActions.Visibility = hasProfile ? Visibility.Visible : Visibility.Collapsed;

        BtnDuplicateMenuProfile.IsEnabled = hasProfile;
        BtnRemoveMenuProfile.IsEnabled = hasProfile;
        BtnAddMenuItem.IsEnabled = hasProfile;

        bool hasItem = _selectedItem is not null;
        int index = ListItems.SelectedIndex;
        BtnRemoveMenuItem.IsEnabled = hasItem;
        BtnMoveItemUp.IsEnabled = hasItem && index > 0;
        BtnMoveItemDown.IsEnabled = hasItem && index >= 0 && index < _menuItems.Count - 1;

        PnlNoItems.Visibility = hasProfile && _menuItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MenuProfileCount.Text = _menuProfiles.Count.ToString(CultureInfo.InvariantCulture);
        MenuItemCount.Text = $"{_menuItems.Count} de {CustomMenuNormalizer.MaxItems}";
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

    /// <summary>Devolve a primeira mensagem de erro de sintaxe (null = tudo certo).</summary>
    private string? ValidateMenus()
    {
        SyncProfileItems();

        foreach (CustomMenuProfile profile in _menuProfiles)
        {
            string name = (profile.Name ?? "").Trim();
            if (profile.Enabled && name.Length == 0)
                return "Dê um nome a todos os menus ativos.";
            if (name.Length > CustomMenuNormalizer.MaxNameLength)
                return $"O nome de um menu passou de {CustomMenuNormalizer.MaxNameLength} caracteres.";

            if (!profile.Enabled) continue;

            foreach (CustomMenuItem item in profile.Items ?? new List<CustomMenuItem>())
            {
                if (item is null || !item.Enabled) continue;

                string label = (item.Label ?? "").Trim();
                if (label.Length == 0)
                    return $"Há um item sem rótulo no menu “{name}”.";
                if (label.Length > CustomMenuNormalizer.MaxLabelLength)
                    return $"O rótulo “{label}” passou de {CustomMenuNormalizer.MaxLabelLength} caracteres.";
                if ((item.Description ?? "").Length > CustomMenuNormalizer.MaxDescriptionLength)
                    return $"A descrição do item “{label}” passou de {CustomMenuNormalizer.MaxDescriptionLength} caracteres.";

                switch (item.Kind)
                {
                    case CustomMenuItemKind.Submenu:
                        string? submenuError = ValidateSubmenu(item, label);
                        if (submenuError is not null) return submenuError;
                        break;

                    default:
                        string? targetError = CustomMenuValidation.ValidateTarget(item, label, "item");
                        if (targetError is not null) return targetError;
                        break;
                }
            }
        }

        return null;
    }

    private static string? ValidateSubmenu(CustomMenuItem item, string label)
    {
        var options = (item.Options ?? new List<CustomMenuItem>())
            .Where(o => o is not null)
            .ToList();

        if (options.Count > CustomMenuNormalizer.MaxOptions)
            return $"O submenu “{label}” passou de {CustomMenuNormalizer.MaxOptions} opções.";

        var active = CustomMenuNormalizer.ActiveOptions(item);
        if (active.Count < CustomMenuNormalizer.MinOptions)
            return $"O submenu “{label}” precisa de pelo menos {CustomMenuNormalizer.MinOptions} opções ativas.";

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
        // Aplica a última edição pendente antes do aviso; o timer não sobrevive ao fechamento.
        if (_previewTimer?.IsEnabled == true)
        {
            if (_menusDirty) RefreshPreview();
            else _previewTimer.Stop();
        }

        List<string> changed = ChangedSections();
        if (changed.Count > 0)
        {
            bool discard = Dialogs.Confirm(this, "Descartar alterações?",
                $"Configurações não salvas: {string.Join(", ", changed)}. Fechar mesmo assim?",
                "Descartar", "Continuar editando", DialogIcon.Warning, defaultIsConfirm: false);
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
            choice = new GlyphChoice(value, "Personalizado");
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
