// ============================================================================
// RadialFlow - Janela de conversão (formato e pasta de destino)
// ----------------------------------------------------------------------------
// Apresenta ao usuário os formatos de destino suportados para o arquivo sob
// o cursor e permite selecionar o diretório de saída.
// ============================================================================

using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using RadialFlow.Security;
using RadialFlow.Services;

namespace RadialFlow.UI;

public partial class ConversionWindow : Window
{
    private readonly string _sourcePath;

    public string? SelectedTargetFormat { get; private set; }
    public string? DestinationFilePath { get; private set; }

    public ConversionWindow(string sourcePath)
    {
        UiTheme.Install(this);   // estilos e cores: antes do XAML, que usa DynamicResource
        UiTheme.StyleTitleBar(this);
        InitializeComponent();
        Icon = AppIconRenderer.WindowIcon;
        _sourcePath = sourcePath;

        TxtFileName.Text = Path.GetFileName(sourcePath);

        var category = ConversionService.DetectCategory(sourcePath);
        TxtCategory.Text = $"Categoria detectada: {GetCategoryName(category)}";

        string? initialDir = Path.GetDirectoryName(sourcePath);
        TxtDestinationDir.Text = initialDir ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        var options = ConversionService.GetAvailableOptions(sourcePath);
        CmbTargetFormat.ItemsSource = options;
        if (options.Count > 0)
            CmbTargetFormat.SelectedIndex = 0;
        else
        {
            BtnConvert.IsEnabled = false;
            TxtCategory.Text += "\nNenhum programa instalado converte este arquivo. Veja no README, em \"Converter arquivos\", " +
                                "o que instalar (sempre em C:\\Program Files, ou com winget --scope machine).";
        }
    }

    private static string GetCategoryName(FileCategory cat) => cat switch
    {
        FileCategory.Image => "Imagem",
        FileCategory.Video => "Vídeo",
        FileCategory.Audio => "Áudio",
        FileCategory.Document => "Documento",
        FileCategory.Spreadsheet => "Planilha",
        FileCategory.Presentation => "Apresentação",
        _ => "Desconhecido / Não suportado"
    };

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Selecionar pasta de destino",
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
            Dialogs.Message(this, "Pasta de destino", "A pasta de destino não existe ou é inválida.", DialogIcon.Warning);
            return;
        }

        string baseName = Path.GetFileNameWithoutExtension(_sourcePath);
        // Nunca sobrescreve um arquivo existente: usa "nome (1).ext" se necessário.
        if (!FileNaming.TryGetUniquePath(Path.Combine(outDir, $"{baseName}.{opt.TargetFormat}"), out string destFile))
        {
            Dialogs.Message(this, "Nome de destino", "Não foi possível montar um nome de arquivo válido para o destino.", DialogIcon.Warning);
            return;
        }

        SelectedTargetFormat = opt.TargetFormat;
        DestinationFilePath = destFile;

        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
