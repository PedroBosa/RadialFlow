// ============================================================================
// RadialFlow - Janela de instalação dos conversores
// ----------------------------------------------------------------------------
// Usada em dois lugares, com o mesmo comportamento:
//   * pelo instalador do RadialFlow: "RadialFlow.exe --install-converters
//     ffmpeg,pandoc" (o app abre só esta janela e sai no fim);
//   * pela aba Conversores das Configurações.
// Mostra o progresso do download e da instalação; no sucesso fecha sozinha em
// poucos segundos; numa falha fica aberta, em vermelho, dizendo o que houve.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using RadialFlow.Infrastructure;
using RadialFlow.Services;

namespace RadialFlow.UI;

internal static class ConverterSetup
{
    /// <summary>Argumento usado pelo instalador.</summary>
    public const string CommandLineSwitch = "--install-converters";

    private static readonly TimeSpan AutoCloseDelay = TimeSpan.FromSeconds(3);

    /// <summary>true se a linha de comando pede a instalação de conversores; <paramref name="list"/> = "ffmpeg,pandoc".</summary>
    public static bool TryGetCommandLine(IReadOnlyList<string> args, out string list)
    {
        list = "";
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg.StartsWith(CommandLineSwitch + "=", StringComparison.OrdinalIgnoreCase))
            {
                list = arg[(CommandLineSwitch.Length + 1)..];
                return true;
            }

            if (arg.Equals(CommandLineSwitch, StringComparison.OrdinalIgnoreCase))
            {
                list = i + 1 < args.Count ? args[i + 1] : "";
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Mostra a janela e instala. Chamar na thread de UI. <paramref name="onFinished"/> recebe true se tudo deu certo,
    /// e só é chamado quando a janela fechou E o trabalho terminou (um instalador do Windows nunca fica órfão).
    /// </summary>
    public static void Run(IReadOnlyList<ConverterPackage> packages, Action<bool>? onFinished = null)
    {
        Dispatcher ui = Dispatcher.CurrentDispatcher;
        string title = packages.Count == 1
            ? Loc.Format("Converters.InstallWindowOne", packages[0].Name)
            : Loc.Get("Converters.InstallWindowMany");
        var window = new ProgressWindow(title);

        bool windowClosed = false, workDone = false, success = false;
        void FinishIfReady()
        {
            if (windowClosed && workDone) onFinished?.Invoke(success);
        }

        window.Closed += (_, _) =>
        {
            windowClosed = true;
            FinishIfReady();
        };
        window.Show();
        window.Activate();

        AppLog.Info("Converters", $"Instalando: {string.Join(", ", packages.Select(p => $"{p.Name} {p.Version}"))}.");
        var token = window.CancellationToken;

        _ = Task.Run(async () =>
        {
            try
            {
                List<ConverterInstallResult> results = await ConverterInstaller.CreateDefault()
                    .InstallManyAsync(packages, window, token).ConfigureAwait(false);

                ToolResolver.ResetCache();
                success = results.All(r => r.Succeeded);
                string summary = ConverterInstaller.Summarize(results);

                if (success) window.Report(new ProgressReport(100.0, summary));
                else window.MarkFailed(summary);

                AppLog.Info("Converters", summary);
                if (success) _ = ui.InvokeAsync(async () =>
                {
                    await Task.Delay(AutoCloseDelay).ConfigureAwait(true);
                    if (!windowClosed) window.Close();
                });
            }
            catch (OperationCanceledException)
            {
                window.MarkFailed(Loc.Get("Converters.InstallCancelled"));
                AppLog.Info("Converters", "Instalação cancelada.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Error("Converters", "Falha inesperada ao instalar os conversores.", ex);
                window.MarkFailed(Loc.Format("Converters.InstallFailed", ex.Message));
            }
            finally
            {
                _ = ui.InvokeAsync(() =>
                {
                    workDone = true;
                    FinishIfReady();
                });
            }
        });
    }
}
