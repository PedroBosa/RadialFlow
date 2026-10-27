// ============================================================================
// RadialFlow - Janela de conversão (formato e pasta de destino)
// ----------------------------------------------------------------------------
// Apresenta os formatos que funcionam para TODOS os arquivos selecionados e
// uma única pasta de destino. Arquivos que já estão no formato escolhido são
// ignorados (e avisados na janela), nunca convertidos "para o mesmo formato".
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using RadialFlow.Infrastructure;
using RadialFlow.Security;
using RadialFlow.Services;

namespace RadialFlow.UI;

public partial class ConversionWindow : Window
{
    private readonly IReadOnlyList<string> _sourcePaths;

    internal ConversionBatchPlan? Plan { get; private set; }

    public ConversionWindow(IReadOnlyList<string> sourcePaths)
    {
        UiTheme.Install(this);   // estilos e cores: antes do XAML, que usa DynamicResource
        UiTheme.StyleTitleBar(this);
        InitializeComponent();
        GlassSurface.Attach(this, GlassImageHost, GlassTint);
        Icon = AppIconRenderer.WindowIcon;
        _sourcePaths = sourcePaths;

        TxtFileName.Text = sourcePaths.Count == 1
            ? Path.GetFileName(sourcePaths[0])
            : Loc.Format("Conversion.FilesSelected", sourcePaths.Count);
        TxtCategory.Text = DescribeSources(sourcePaths);

        string? initialDir = sourcePaths.Count > 0 ? Path.GetDirectoryName(sourcePaths[0]) : null;
        TxtDestinationDir.Text = initialDir ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        bool allFiles = sourcePaths.Count > 0;
        foreach (string path in sourcePaths)
        {
            if (!File.Exists(path))
            {
                allFiles = false;
                break;
            }
        }

        var options = allFiles
            ? ConversionBatch.GetCommonOptions(sourcePaths, ConversionService.GetAvailableOptions)
            : new List<ConversionOption>();

        CmbTargetFormat.ItemsSource = options;
        if (options.Count > 0)
        {
            CmbTargetFormat.SelectedIndex = 0;
            CmbTargetFormat.SelectionChanged += (_, _) => UpdateBatchNote();
            UpdateBatchNote();
        }
        else
        {
            BtnConvert.IsEnabled = false;
            TxtCategory.Text += allFiles
                ? "\n" + Loc.Get("Conversion.NoCommonFormat")
                : "\n" + Loc.Get("Conversion.FilesOnly");
        }
    }

    private static string DescribeSources(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return Loc.Get("Conversion.NoneSelected");

        var categories = new List<string>();
        foreach (string path in paths)
        {
            string name = GetCategoryName(ConversionService.DetectCategory(path));
            if (!categories.Contains(name)) categories.Add(name);
        }

        if (paths.Count == 1) return Loc.Format("Conversion.CategoryDetected", categories[0]);
        return Loc.Format("Conversion.FilesWithCategory", paths.Count, string.Join(", ", categories));
    }

    private static string GetCategoryName(FileCategory cat) => cat switch
    {
        FileCategory.Image => Loc.Get("Conversion.Category.Image"),
        FileCategory.Video => Loc.Get("Conversion.Category.Video"),
        FileCategory.Audio => Loc.Get("Conversion.Category.Audio"),
        FileCategory.Document => Loc.Get("Conversion.Category.Document"),
        FileCategory.Spreadsheet => Loc.Get("Conversion.Category.Spreadsheet"),
        FileCategory.Presentation => Loc.Get("Conversion.Category.Presentation"),
        _ => Loc.Get("Conversion.Category.Unknown")
    };

    private void UpdateBatchNote()
    {
        TxtBatchNote.Visibility = Visibility.Collapsed;
        if (_sourcePaths.Count < 2 || CmbTargetFormat.SelectedItem is not ConversionOption opt) return;

        int already = 0;
        foreach (string path in _sourcePaths)
            if (ConversionFormats.SameFormat(Path.GetExtension(path), opt.TargetFormat)) already++;

        if (already == 0) return;

        string target = opt.TargetFormat.ToUpperInvariant();
        TxtBatchNote.Text = already == 1
            ? Loc.Format("Conversion.AlreadyInFormatOne", target)
            : Loc.Format("Conversion.AlreadyInFormatMany", already, target);
        TxtBatchNote.Visibility = Visibility.Visible;
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = Loc.Get("Conversion.PickFolderTitle"),
            InitialDirectory = TxtDestinationDir.Text
        };

        if (dlg.ShowDialog() == true)
        {
            TxtDestinationDir.Text = dlg.FolderName;
        }
    }

    private void BtnConvert_Click(object sender, RoutedEventArgs e)
    {
        if (CmbTargetFormat.SelectedItem is not ConversionOption opt) return;

        // Pasta digitada pelo usuário: precisa ser absoluta e canônica (sem "..", ADS, nomes reservados).
        if (!TrustedPath.TryGetSafeFullPath(TxtDestinationDir.Text.Trim(), out string outDir) || !Directory.Exists(outDir))
        {
            Dialogs.Message(this, Loc.Get("Conversion.DestinationTitle"), Loc.Get("Conversion.DestinationInvalid"), DialogIcon.Warning);
            return;
        }

        if (!ConversionBatch.TryPlan(_sourcePaths, opt.TargetFormat, outDir, out ConversionBatchPlan? plan, out string? error) ||
            plan is null)
        {
            Dialogs.Message(this, Loc.Get("Conversion.DialogTitle"), error ?? Loc.Get("Conversion.PrepareFailed"), DialogIcon.Warning);
            return;
        }

        Plan = plan;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
