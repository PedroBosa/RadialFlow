using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using RadialFlow.Config;
using RadialFlow.Infrastructure;
using RadialFlow.UI;

namespace RadialFlow;

public sealed partial class App : Application, IDisposable
{
    /// <summary>Nome lógico da instância única (ver <see cref="SingleInstance"/>).</summary>
    internal const string InstanceName = "RadialFlow";

    private SingleInstance? _instance;
    private RadialController? _radial;
    private TrayIconService? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Registrar ANTES de qualquer outra coisa: se algo cair já na partida, fica no log.
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Instância única: a segunda cópia só pede à primeira para abrir as Configurações e sai.
        _instance = SingleInstance.TryAcquire(InstanceName);
        if (_instance is null)
        {
            AppLog.Info("App", "Outra instância já está em execução; pedindo para abrir as Configurações e encerrando.");
            SingleInstance.SignalExisting(InstanceName);
            AppLog.Shutdown();
            Shutdown(0);
            return;
        }

        AppLog.Info("App", $"Iniciando {AppInfo.DisplayName} (processo {Environment.ProcessId}, " +
                           $"{(Services.UnelevatedLauncher.IsElevated() ? "elevado" : "sem elevação")}, " +
                           $"instância única {(_instance.IsGuarded ? "ativa" : "indisponível")}).");

        try
        {
            // Carrega configurações persistentes
            var settings = SettingsService.Load();

            _radial = new RadialController();
            _radial.ApplySettings(settings);
            _radial.Start();

            _tray = new TrayIconService(_radial);
            _tray.Start();
        }
        catch (Exception ex)
        {
            // Sem hook ou sem bandeja o app ficaria invisível e inútil: avisa e encerra liberando o que subiu.
            AppLog.Error("App", "Falha na inicialização.", ex);
            ReleaseServices();
            AppLog.Flush(TimeSpan.FromSeconds(2));
            MessageBox.Show($"O RadialFlow não pôde iniciar:\n{ex.Message}\n\nDetalhes: {AppLog.FilePath}", "RadialFlow",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // A segunda cópia (ou o atalho do menu Iniciar) abre as Configurações desta.
        _instance.StartListening(() => Dispatcher.InvokeAsync(() => _tray?.OpenSettings()));

        // Só depois de iniciar: uma falha na inicialização não deve ser mascarada.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppLog.Info("App", "Pronto.");
    }

    /// <summary>Libera os serviços (hooks, bandeja). Idempotente: o OnExit já chama; o Dispose cobre usos fora do ciclo normal.</summary>
    public void Dispose() => ReleaseServices();

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseServices();
        AppLog.Info("App", $"Encerrando (código {e.ApplicationExitCode}).");
        AppLog.Shutdown();   // grava o que restava na fila
        base.OnExit(e);
    }

    /// <summary>Bandeja (Shell_NotifyIcon/HICON/HWND), depois hooks (WinEvent + mouse), depois a trava de instância. Idempotente.</summary>
    private void ReleaseServices()
    {
        _tray?.Dispose();
        _tray = null;
        _radial?.Dispose();
        _radial = null;
        _instance?.Dispose();
        _instance = null;
    }

    /// <summary>
    /// Rede de segurança da thread de UI (callbacks de janela, timers, hooks de HwndSource).
    /// Um app de bandeja que cai leva junto o hook do mouse: registra e continua.
    /// </summary>
    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("App", "Exceção não tratada na UI.", e.Exception);
        e.Handled = true;
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error("App", "Exceção não observada em tarefa.", e.Exception);
        e.SetObserved();
    }

    /// <summary>Exceção fatal fora da UI: não dá para impedir o encerramento, mas fica registrada (e gravada em disco).</summary>
    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        AppLog.Error("App", $"Exceção fatal (encerrando={e.IsTerminating}).", e.ExceptionObject as Exception);
        AppLog.Flush(TimeSpan.FromSeconds(2));   // o processo vai morrer: grava antes
    }
}
