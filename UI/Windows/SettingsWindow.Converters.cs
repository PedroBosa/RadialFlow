// ============================================================================
// RadialFlow - Configurações: aba Conversores
// ----------------------------------------------------------------------------
// Uma linha por programa (ConverterPackages): o que ele faz, o tamanho do
// download e se já está instalado. Os marcados são instalados pela mesma janela
// de progresso usada pelo instalador (ConverterSetup).
// A verificação (disco e permissões das pastas) roda fora da UI.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using RadialFlow.Infrastructure;
using RadialFlow.Services;

namespace RadialFlow.UI;

public partial class SettingsWindow
{
    private readonly Dictionary<ConverterPackage, CheckBox> _converterChecks = new();
    private bool _installingConverters;

    private void InitializeConvertersTab()
    {
        foreach (ConverterPackage package in ConverterPackages.All)
        {
            var check = new CheckBox { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0), IsEnabled = false };
            AutomationProperties.SetName(check, $"Instalar o {package.Name}");
            check.Checked += (_, _) => UpdateInstallButton();
            check.Unchecked += (_, _) => UpdateInstallButton();
            _converterChecks[package] = check;

            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = package.Name, Style = (Style)FindResource("CardTitle") });
            text.Children.Add(new TextBlock { Text = package.Purpose, Style = (Style)FindResource("CardHint"), Margin = new Thickness(0, 2, 0, 0) });
            text.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("CardHint"), Margin = new Thickness(0, 4, 0, 0), Tag = package,
                Text = $"Versão {package.Version} · download de {package.SizeText} · licença {package.License}"
            });

            var row = new DockPanel();
            DockPanel.SetDock(check, Dock.Left);
            row.Children.Add(check);
            row.Children.Add(text);
            ConverterList.Children.Add(new Border { Style = (Style)FindResource("Card"), Child = row });
        }

        // No Loaded: aí o dispatcher já está rodando e a continuação volta para a thread da janela.
        Loaded += (_, _) => _ = RefreshConvertersAsync();
    }

    /// <summary>Atualiza a lista: marca e trava os já instalados; mostra por que não dá para instalar, se for o caso.</summary>
    private async Task RefreshConvertersAsync()
    {
        BtnInstallConverters.IsEnabled = false;
        BtnRefreshConverters.IsEnabled = false;
        ConverterStatus.Text = "Verificando o que está instalado...";

        var installer = ConverterInstaller.CreateDefault();
        Dictionary<ConverterPackage, bool> installed;
        string? blocked;
        int imageFormats;
        try
        {
            (installed, blocked, imageFormats) = await Task.Run(() =>
            {
                ToolResolver.ResetCache();
                return (ConverterPackages.All.ToDictionary(p => p, installer.IsInstalled), ConverterInstaller.CheckCanInstall(),
                        WicCodecs.Readable.Count);
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Error("Settings", "Falha ao verificar os conversores.", ex);
            ConverterStatus.Text = $"Não foi possível verificar os conversores: {ex.Message}";
            BtnRefreshConverters.IsEnabled = true;
            return;
        }

        foreach (var (package, check) in _converterChecks)
        {
            bool has = installed[package];
            check.IsChecked = has;
            check.IsEnabled = !has && blocked is null && !_installingConverters;
            check.ToolTip = has ? "Já instalado" : null;
            if (check.Parent is DockPanel row && row.Children[1] is StackPanel text && text.Children[0] is TextBlock name)
                name.Text = has ? $"{package.Name}  ✓ instalado" : package.Name;
        }

        bool anyMissing = installed.Values.Any(v => !v);
        ConverterStatus.Text = blocked ?? (anyMissing
            ? "Marque os programas que quiser e clique em Instalar. Precisa de internet; o LibreOffice é grande e pode levar alguns minutos."
            : "Todos os conversores estão instalados.");
        WindowsCodecsStatus.Text = $"Neste computador o Windows lê {imageFormats} formatos de imagem.";

        BtnRefreshConverters.IsEnabled = !_installingConverters;
        UpdateInstallButton();
    }

    private List<ConverterPackage> SelectedConverters() =>
        _converterChecks.Where(kv => kv.Value.IsEnabled && kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();

    private void UpdateInstallButton() =>
        BtnInstallConverters.IsEnabled = !_installingConverters && SelectedConverters().Count > 0;

    private void BtnRefreshConverters_Click(object sender, RoutedEventArgs e) => _ = RefreshConvertersAsync();

    private void BtnInstallConverters_Click(object sender, RoutedEventArgs e)
    {
        List<ConverterPackage> chosen = SelectedConverters();
        if (chosen.Count == 0) return;

        long bytes = chosen.Sum(p => p.SizeBytes);
        string names = string.Join(", ", chosen.Select(p => p.Name));
        if (!Dialogs.Confirm(this, "Instalar conversores",
                $"Instalar {names}? O download tem cerca de {Math.Round(bytes / 1048576.0):0} MB e vem dos sites oficiais. " +
                "Você pode continuar usando o computador enquanto isso.", "Instalar", "Cancelar"))
            return;

        _installingConverters = true;
        BtnInstallConverters.IsEnabled = false;
        BtnRefreshConverters.IsEnabled = false;
        foreach (CheckBox check in _converterChecks.Values) check.IsEnabled = false;
        ConverterStatus.Text = "Instalando... acompanhe na janela de progresso.";
        AppLog.Info("Settings", $"Instalação de conversores pedida: {names}.");

        ConverterSetup.Run(chosen, succeeded =>
        {
            _installingConverters = false;
            AppLog.Info("Settings", $"Instalação de conversores terminou ({(succeeded ? "sucesso" : "com falhas")}).");
            if (IsLoaded) _ = RefreshConvertersAsync();
        });
    }
}
