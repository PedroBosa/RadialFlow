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
using Microsoft.Win32;
using RadialFlow.Config;
using RadialFlow.Context;
using RadialFlow.Security;
using RadialFlow.Services;

namespace RadialFlow.UI;

/// <summary>Item do seletor de ícones (par de glifo Segoe + nome amigável).</summary>
public sealed record GlyphChoice(string Glyph, string Label);

public partial class SettingsWindow
{
    private const string DefaultGlyph = "\uE734";

    private static readonly (RadialAction Action, string Label)[] ActionChoices =
    [
        (RadialAction.Convert, "Converter"),
        (RadialAction.Compress, "Comprimir"),
        (RadialAction.CopyPath, "Copiar caminho"),
        (RadialAction.MoveTo, "Mover para"),
        (RadialAction.SendTo, "Enviar para"),
        (RadialAction.Rename, "Renomear"),
        (RadialAction.FavoriteApps, "Apps favoritos"),
        (RadialAction.SystemControls, "Ajustes rápidos"),
        (RadialAction.ClipboardHistory, "Área de transferência"),
        (RadialAction.LockScreen, "Bloquear"),
        (RadialAction.SnapLeft, "Snap à esquerda"),
        (RadialAction.SnapRight, "Snap à direita"),
        (RadialAction.Maximize, "Maximizar"),
        (RadialAction.Minimize, "Minimizar"),
        (RadialAction.Close, "Fechar"),
        (RadialAction.AlwaysOnTop, "Sempre no topo"),
        (RadialAction.Transparency, "Transparência"),
        (RadialAction.Paste, "Colar"),
        (RadialAction.Translate, "Traduzir"),
        (RadialAction.Clear, "Limpar"),
        (RadialAction.SearchWeb, "Pesquisar na web"),
        (RadialAction.Format, "Formatar (negrito)"),
    ];

    private static readonly (CustomMenuItemKind Kind, string Label)[] KindChoices =
    [
        (CustomMenuItemKind.Action, "Ação do RadialFlow"),
        (CustomMenuItemKind.Launch, "Abrir executável"),
        (CustomMenuItemKind.OpenUrl, "Abrir site (URL)"),
        (CustomMenuItemKind.OpenPath, "Abrir arquivo ou pasta"),
        (CustomMenuItemKind.Hotkey, "Enviar atalho"),
        (CustomMenuItemKind.Text, "Colar texto"),
    ];

    private static readonly (string Glyph, string Label)[] GlyphChoices =
    [
        ("\uE734", "Aplicativos"),
        ("\uE70F", "Editar"),
        ("\uE8A5", "Documento"),
        ("\uEB9F", "Imagem"),
        ("\uE714", "Vídeo"),
        ("\uE8D6", "Áudio"),
        ("\uF012", "Arquivo zip"),
        ("\uE8AB", "Converter"),
        ("\uE8AC", "Renomear"),
        ("\uE838", "Mover"),
        ("\uE724", "Enviar"),
        ("\uE71B", "Copiar"),
        ("\uE8B7", "Pasta"),
        ("\uE774", "Traduzir"),
        ("\uE721", "Pesquisar"),
        ("\uE77F", "Colar"),
        ("\uE72E", "Bloquear"),
        ("\uE713", "Ajustes"),
        ("\uE8BB", "Fechar"),
        ("\uE922", "Maximizar"),
        ("\uE921", "Minimizar"),
        ("\uE718", "Fixar"),
        ("\uE706", "Transparência"),
        ("\uE8DD", "Negrito"),
        ("\uE894", "Limpar"),
    ];

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
        new(GlyphChoices.Select(g => new GlyphChoice(g.Glyph, g.Label)));

    private ObservableCollection<CustomMenuProfile> _menuProfiles = new();
    private readonly ObservableCollection<CustomMenuItem> _menuItems = new();
    private CustomMenuProfile? _selectedProfile;
    private CustomMenuItem? _selectedItem;
    private MenuPreview? _preview;
    private bool _loadingMenus;
    private bool _menusDirty;

    private void InitializeMenusTab()
    {
        _loadingMenus = true;
        try
        {
            _preview = new MenuPreview();
            MenuPreviewHost.Child = _preview;
            CmbItemGlyph.ItemsSource = _glyphChoices;

            foreach (var (kind, label) in KindChoices)
                CmbItemKind.Items.Add(new ComboBoxItem { Content = label, Tag = kind });

            foreach (var (action, label) in ActionChoices)
                CmbItemAction.Items.Add(new ComboBoxItem { Content = label, Tag = action });

            foreach (var (kind, label) in ContextChoices)
            {
                var check = new CheckBox { Content = label, Tag = kind, Margin = new Thickness(0, 4, 14, 4) };
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
        PnlProfileEditor.IsEnabled = profile is not null;

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

        MarkMenusDirty();
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

        MarkMenusDirty();
    }

    private void TxtItemLabel_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loadingMenus) return;
        RefreshItemListSelection();
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
        PreviewSummary.Text = $"{automatic.Count} fatias automáticas · {replacements} substituição(ões) · {extras} extra(s).";

        if (plan.Dropped.Count == 0)
        {
            PreviewWarning.Text = "";
            PreviewWarning.Visibility = Visibility.Collapsed;
        }
        else
        {
            PreviewWarning.Text = plan.Dropped.Count == 1
                ? "1 item será ignorado (limite de 8 fatias)."
                : $"{plan.Dropped.Count} itens serão ignorados (limite de 8 fatias).";
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
        ContextInfo ctx = new(kind, IntPtr.Zero, "", 0, 0,
            kind == RadialContextKind.FileOrFolder ? new[] { @"C:\preview.png" } : ContextInfo.NoPaths);

        // Dependências de mentira: a prévia precisa das MESMAS fatias automáticas, mas não pode tocar em disco.
        var deps = new SliceFactoryDependencies(
            PreviewSliceActions.Instance,
            _ => new[] { new ConversionOption("pdf", "PDF — prévia", "Prévia") },
            () => new[]
            {
                new FavoriteAppEntry("App de exemplo", @"C:\app.exe"),
                new FavoriteAppEntry("Outro app", @"C:\outro.exe"),
            },
            _ => true,
            null);

        return SliceFactory.Build(ctx, deps);
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
        bool hasProfile = _selectedProfile is not null;
        BtnDuplicateMenuProfile.IsEnabled = hasProfile;
        BtnRemoveMenuProfile.IsEnabled = hasProfile;
        BtnAddMenuItem.IsEnabled = hasProfile;

        bool hasItem = _selectedItem is not null;
        int index = ListItems.SelectedIndex;
        BtnRemoveMenuItem.IsEnabled = hasItem;
        BtnMoveItemUp.IsEnabled = hasItem && index > 0;
        BtnMoveItemDown.IsEnabled = hasItem && index >= 0 && index < _menuItems.Count - 1;
    }

    private void MarkMenusDirty()
    {
        if (_loadingMenus) return;
        _menusDirty = true;
        RefreshPreview();
        UpdateMenuButtons();
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
                    case CustomMenuItemKind.Action:
                        if (!Enum.IsDefined(item.Action))
                            return $"A ação do item “{label}” não existe mais.";
                        break;

                    case CustomMenuItemKind.Launch:
                        if (string.IsNullOrWhiteSpace(item.Target))
                            return $"Informe o executável do item “{label}”.";
                        break;

                    case CustomMenuItemKind.OpenUrl:
                        if (!UnelevatedLauncher.IsSafeUrl(item.Target))
                            return $"A URL do item “{label}” precisa começar com http:// ou https://.";
                        break;

                    case CustomMenuItemKind.OpenPath:
                        if (string.IsNullOrWhiteSpace(item.Target))
                            return $"Informe o arquivo ou a pasta do item “{label}”.";
                        if (!TrustedPath.TryGetSafeFullPath(item.Target, out _))
                            return $"O caminho do item “{label}” é inválido.";
                        break;

                    case CustomMenuItemKind.Hotkey:
                        if (!CustomActionLogic.TryParseHotkey(item.Target, out _, out string? reason))
                            return $"Atalho do item “{label}”: {reason}";
                        break;

                    case CustomMenuItemKind.Text:
                        if (string.IsNullOrEmpty(item.Text))
                            return $"Informe o texto a colar no item “{label}”.";
                        if (item.Text.Length > CustomMenuNormalizer.MaxTextLength)
                            return $"O texto do item “{label}” passou de {CustomMenuNormalizer.MaxTextLength} caracteres.";
                        break;
                }
            }
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
            }
        }

        settings.CustomProfiles = _menuProfiles.Select(CloneProfile).ToList();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_menusDirty)
        {
            bool discard = Dialogs.Confirm(this, "Descartar alterações?",
                "As alterações nos menus personalizados ainda não foram salvas. Fechar mesmo assim?",
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
        public void ConvertTo(string sourceFile, string targetFormat) { }
        public void LaunchFavorite(FavoriteAppEntry app) { }
        public void RunCustom(CustomMenuItem item, ContextInfo context) { }
    }
}
