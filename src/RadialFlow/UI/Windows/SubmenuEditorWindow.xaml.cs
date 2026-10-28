// ============================================================================
// RadialFlow - Diálogo de opções do submenu
// ----------------------------------------------------------------------------
// Edita as opções do segundo anel de um item do tipo Submenu. Trabalha numa
// cópia: só "Concluir" devolve as opções ao editor da aba Menus, que ainda
// passa pela validação e pelo normalizador antes de salvar.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RadialFlow.Config;
using RadialFlow.Context;
using RadialFlow.Infrastructure;

namespace RadialFlow.UI;

public partial class SubmenuEditorWindow : Window
{
    private readonly ObservableCollection<CustomMenuItem> _options = new();
    private readonly ObservableCollection<GlyphChoice> _glyphChoices =
        new(CustomMenuChoices.Glyphs.Select(g => new GlyphChoice(g.Glyph, g.Label)));

    private readonly string _submenuLabel;
    private CustomMenuItem? _selected;
    private bool _loading;

    /// <summary>Só para testes: chamado quando a janela termina de renderizar, antes de ShowDialog bloquear.</summary>
    internal static Action<SubmenuEditorWindow>? BeforeShowForTests { get; set; }

    internal SubmenuEditorWindow(string submenuLabel, IEnumerable<CustomMenuItem> options)
    {
        UiTheme.Install(this);   // estilos e cores: antes do XAML, que usa DynamicResource
        UiTheme.StyleTitleBar(this);
        InitializeComponent();
        Icon = AppIconRenderer.WindowIcon;

        _submenuLabel = string.IsNullOrWhiteSpace(submenuLabel) ? Loc.Get("Submenu.ThisItem") : submenuLabel.Trim();
        TxtSubtitle.Text = Loc.Format("Submenu.SubtitleFormat", _submenuLabel);

        foreach (CustomMenuItem option in options ?? Array.Empty<CustomMenuItem>())
            if (option is not null) _options.Add(Clone(option));

        ListOptions.ItemsSource = _options;
        CmbOptionGlyph.ItemsSource = _glyphChoices;

        foreach (var (kind, label) in CustomMenuChoices.Kinds)
            if (kind is not (CustomMenuItemKind.Submenu or CustomMenuItemKind.Routine))
                CmbOptionKind.Items.Add(new ComboBoxItem { Content = label, Tag = kind });

        foreach (var (action, label) in CustomMenuChoices.Actions)
            CmbOptionAction.Items.Add(new ComboBoxItem { Content = label, Tag = action });

        _loading = true;
        try
        {
            if (_options.Count > 0)
            {
                ListOptions.SelectedIndex = 0;
                LoadOptionEditor(_options[0]);
            }
            else
            {
                LoadOptionEditor(null);
            }
        }
        finally
        {
            _loading = false;
        }

        UpdateState();
    }

    /// <summary>Opções editadas, na ordem do segundo anel. Válido apenas quando o diálogo devolve true.</summary>
    internal IReadOnlyList<CustomMenuItem> Options => _options.ToList();

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        BeforeShowForTests?.Invoke(this);
    }

    // ------------------------------------------------------------------
    // Lista
    // ------------------------------------------------------------------

    private void ListOptions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        _loading = true;
        try
        {
            LoadOptionEditor(ListOptions.SelectedItem as CustomMenuItem);
        }
        finally
        {
            _loading = false;
        }

        UpdateState();
    }

    private void LoadOptionEditor(CustomMenuItem? option)
    {
        _selected = option;
        BdOptionEditor.Visibility = option is null ? Visibility.Collapsed : Visibility.Visible;
        PnlNoOptionSelected.Visibility = option is null ? Visibility.Visible : Visibility.Collapsed;

        if (option is null)
        {
            TxtOptionLabel.Text = "";
            TxtOptionDescription.Text = "";
            ChkOptionEnabled.IsChecked = false;
            CmbOptionGlyph.SelectedItem = null;
            CmbOptionKind.SelectedIndex = -1;
            CmbOptionAction.SelectedIndex = -1;
            TxtOptionLaunch.Text = "";
            TxtOptionUrl.Text = "";
            TxtOptionPath.Text = "";
            TxtOptionHotkey.Text = "";
            TxtOptionText.Text = "";
            SetKindPanels(CustomMenuItemKind.Action);
            return;
        }

        TxtOptionLabel.Text = option.Label ?? "";
        TxtOptionDescription.Text = option.Description ?? "";
        ChkOptionEnabled.IsChecked = option.Enabled;
        SelectGlyph(option.Glyph);
        SelectKind(option.Kind);
        SelectAction(option.Action);
        TxtOptionLaunch.Text = option.Kind == CustomMenuItemKind.Launch ? option.Target ?? "" : "";
        TxtOptionUrl.Text = option.Kind == CustomMenuItemKind.OpenUrl ? option.Target ?? "" : "";
        TxtOptionPath.Text = option.Kind == CustomMenuItemKind.OpenPath ? option.Target ?? "" : "";
        TxtOptionHotkey.Text = option.Kind == CustomMenuItemKind.Hotkey ? option.Target ?? "" : "";
        TxtOptionText.Text = option.Kind == CustomMenuItemKind.Text ? option.Text ?? "" : "";
        SetKindPanels(option.Kind);
    }

    private void BtnAddOption_Click(object sender, RoutedEventArgs e)
    {
        if (_options.Count >= CustomMenuNormalizer.MaxOptions)
        {
            Dialogs.Message(this, Loc.Get("Submenu.Title"),
                Loc.Format("Submenu.MaxOptions", CustomMenuNormalizer.MaxOptions), DialogIcon.Warning);
            return;
        }

        var option = new CustomMenuItem
        {
            Label = UniqueName(_options.Select(o => o.Label), Loc.Get("Submenu.NewOption")),
            Kind = CustomMenuItemKind.Action,
            Action = RadialAction.Convert,
        };

        _options.Add(option);
        ListOptions.SelectedItem = option;
        ListOptions.ScrollIntoView(option);
        UpdateState();
    }

    private void BtnRemoveOption_Click(object sender, RoutedEventArgs e)
    {
        int index = ListOptions.SelectedIndex;
        if (index < 0 || index >= _options.Count) return;

        _options.RemoveAt(index);
        if (_options.Count > 0)
            ListOptions.SelectedIndex = Math.Min(index, _options.Count - 1);
        else
            LoadOptionEditor(null);

        UpdateState();
    }

    private void BtnOptionUp_Click(object sender, RoutedEventArgs e) => MoveOption(-1);

    private void BtnOptionDown_Click(object sender, RoutedEventArgs e) => MoveOption(+1);

    private void MoveOption(int delta)
    {
        int index = ListOptions.SelectedIndex;
        int target = index + delta;
        if (index < 0 || target < 0 || target >= _options.Count) return;

        CustomMenuItem option = _options[index];
        _options.Move(index, target);
        ListOptions.SelectedItem = option;
        UpdateState();
    }

    // ------------------------------------------------------------------
    // Campos da opção
    // ------------------------------------------------------------------

    private void CmbOptionKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CustomMenuItemKind kind = SelectedKind();
        SetKindPanels(kind);

        if (_loading || _selected is null) return;

        _selected.Kind = kind;
        _selected.Target = "";
        _selected.Text = "";

        _loading = true;
        try
        {
            TxtOptionLaunch.Text = "";
            TxtOptionUrl.Text = "";
            TxtOptionPath.Text = "";
            TxtOptionHotkey.Text = "";
            TxtOptionText.Text = "";
        }
        finally
        {
            _loading = false;
        }

        RefreshOptionRow();
        UpdateState();
    }

    private void OptionField_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _selected is null) return;

        CustomMenuItemKind kind = SelectedKind();
        _selected.Kind = kind;
        _selected.Label = TxtOptionLabel.Text;
        _selected.Description = TxtOptionDescription.Text;
        if (CmbOptionAction.SelectedItem is ComboBoxItem { Tag: RadialAction action })
            _selected.Action = action;

        _selected.Target = kind switch
        {
            CustomMenuItemKind.Launch => TxtOptionLaunch.Text,
            CustomMenuItemKind.OpenUrl => TxtOptionUrl.Text,
            CustomMenuItemKind.OpenPath => TxtOptionPath.Text,
            CustomMenuItemKind.Hotkey => TxtOptionHotkey.Text,
            _ => "",
        };
        _selected.Text = kind == CustomMenuItemKind.Text ? TxtOptionText.Text : "";

        UpdateState();
    }

    private void OptionVisual_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _selected is null) return;

        _selected.Glyph = (CmbOptionGlyph.SelectedItem as GlyphChoice)?.Glyph ?? DefaultGlyph;
        _selected.Enabled = ChkOptionEnabled.IsChecked == true;

        RefreshOptionRow();
        UpdateState();
    }

    private void TxtOptionLabel_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || _selected is null) return;
        RefreshOptionRow();
        UpdateState();
    }

    private void RefreshOptionRow()
    {
        if (_selected is null) return;

        _loading = true;
        try
        {
            ListOptions.Items.Refresh();
            ListOptions.SelectedItem = _selected;
        }
        finally
        {
            _loading = false;
        }
    }

    private void UpdateState()
    {
        TxtOptionCount.Text = Loc.Format("Menus.SubmenuCountFormat", _options.Count, CustomMenuNormalizer.MaxOptions);
        PnlNoOptions.Visibility = _options.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        bool hasSelection = _selected is not null;
        int index = ListOptions.SelectedIndex;
        BtnAddOption.IsEnabled = _options.Count < CustomMenuNormalizer.MaxOptions;
        BtnRemoveOption.IsEnabled = hasSelection;
        BtnOptionUp.IsEnabled = hasSelection && index > 0;
        BtnOptionDown.IsEnabled = hasSelection && index >= 0 && index < _options.Count - 1;

        int active = _options.Count(o => o.Enabled);
        if (active >= CustomMenuNormalizer.MinOptions)
        {
            TxtOptionsWarning.Text = "";
            TxtOptionsWarning.Visibility = Visibility.Collapsed;
        }
        else
        {
            TxtOptionsWarning.Text = active == 0
                ? Loc.Get("Submenu.WarnNoneActive")
                : Loc.Get("Submenu.WarnOneActive");
            TxtOptionsWarning.Visibility = Visibility.Visible;
        }
    }

    // ------------------------------------------------------------------
    // Concluir
    // ------------------------------------------------------------------

    private void BtnDone_Click(object sender, RoutedEventArgs e)
    {
        var active = _options.Where(o => o.Enabled).ToList();
        if (active.Count < CustomMenuNormalizer.MinOptions)
        {
            Dialogs.Message(this, Loc.Get("Submenu.Title"),
                Loc.Format("Submenu.MinActive", CustomMenuNormalizer.MinOptions),
                DialogIcon.Warning);
            return;
        }

        foreach (CustomMenuItem option in active)
        {
            string? error = CustomMenuValidation.ValidateOption(option, _submenuLabel);
            if (error is null) continue;

            _loading = true;
            try
            {
                ListOptions.SelectedItem = option;
                LoadOptionEditor(option);
            }
            finally
            {
                _loading = false;
            }

            Dialogs.Message(this, Loc.Get("Submenu.Title"), error, DialogIcon.Warning);
            return;
        }

        foreach (CustomMenuItem option in _options)
        {
            option.Label = (option.Label ?? "").Trim();
            option.Description = (option.Description ?? "").Trim();
            option.Target = (option.Target ?? "").Trim();
            option.Text ??= "";
        }

        DialogResult = true;
    }

    // ------------------------------------------------------------------
    // Seletores de alvo
    // ------------------------------------------------------------------

    private void BtnBrowseOptionExecutable_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.Get("Menus.PickExecutable"),
            Filter = Loc.Get("Menus.ExecutableFilter"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true) TxtOptionLaunch.Text = dialog.FileName;
    }

    private void BtnBrowseOptionFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.Get("Menus.PickFile"),
            Filter = Loc.Get("Menus.AllFilesFilter"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true) TxtOptionPath.Text = dialog.FileName;
    }

    private void BtnBrowseOptionFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.Get("Menus.PickFolder"), Multiselect = false };
        if (dialog.ShowDialog(this) == true) TxtOptionPath.Text = dialog.FolderName;
    }

    // ------------------------------------------------------------------
    // Utilitários
    // ------------------------------------------------------------------

    private const string DefaultGlyph = "\uE734";

    private static CustomMenuItem Clone(CustomMenuItem item) => new()
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
            choice = new GlyphChoice(value, Loc.Get("Menus.CustomGlyph"));
            _glyphChoices.Add(choice);
        }

        CmbOptionGlyph.SelectedItem = choice;
    }

    private void SelectKind(CustomMenuItemKind kind)
    {
        foreach (ComboBoxItem entry in CmbOptionKind.Items)
        {
            if (entry.Tag is CustomMenuItemKind candidate && candidate == kind)
            {
                CmbOptionKind.SelectedItem = entry;
                return;
            }
        }

        CmbOptionKind.SelectedIndex = 0;
    }

    private void SelectAction(RadialAction action)
    {
        foreach (ComboBoxItem entry in CmbOptionAction.Items)
        {
            if (entry.Tag is RadialAction candidate && candidate == action)
            {
                CmbOptionAction.SelectedItem = entry;
                return;
            }
        }

        CmbOptionAction.SelectedIndex = 0;
    }

    private CustomMenuItemKind SelectedKind() =>
        CmbOptionKind.SelectedItem is ComboBoxItem { Tag: CustomMenuItemKind kind } ? kind : CustomMenuItemKind.Action;

    private void SetKindPanels(CustomMenuItemKind kind)
    {
        PnlOptionAction.Visibility = kind == CustomMenuItemKind.Action ? Visibility.Visible : Visibility.Collapsed;
        PnlOptionLaunch.Visibility = kind == CustomMenuItemKind.Launch ? Visibility.Visible : Visibility.Collapsed;
        PnlOptionUrl.Visibility = kind == CustomMenuItemKind.OpenUrl ? Visibility.Visible : Visibility.Collapsed;
        PnlOptionPath.Visibility = kind == CustomMenuItemKind.OpenPath ? Visibility.Visible : Visibility.Collapsed;
        PnlOptionHotkey.Visibility = kind == CustomMenuItemKind.Hotkey ? Visibility.Visible : Visibility.Collapsed;
        PnlOptionText.Visibility = kind == CustomMenuItemKind.Text ? Visibility.Visible : Visibility.Collapsed;
    }
}
