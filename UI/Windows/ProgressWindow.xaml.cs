// ============================================================================
// RadialFlow - Janela de progresso
// ----------------------------------------------------------------------------
// Exibe barra de progresso suave em segundo plano para compactações e
// conversões sem travar a interface gráfica nem a roda radial.
//
// THREADS: Report/MarkFailed são chamados pelas threads de trabalho. Nada aqui
// bloqueia o trabalhador: o último relatório é guardado e UMA atualização é
// enfileirada na UI (_ui.InvokeAsync, coalescida). Antes era um
// Dispatcher.Invoke síncrono a cada bloco de 80 KB.
//
// SEGURANÇA: "Abrir Pasta" usa UnelevatedLauncher.ShowInExplorer, que valida o
// caminho em TrustedPath e abre o Explorer com o token do usuário (o RadialFlow
// é administrador; antes era Process.Start("explorer.exe") por nome relativo).
// ============================================================================

using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using RadialFlow.Infrastructure;
using RadialFlow.Services;

namespace RadialFlow.UI;

/// <remarks>
/// O ciclo de vida de uma janela WPF é <see cref="Window.Close"/>, não Dispose: o CancellationTokenSource é
/// liberado em <see cref="OnClosed"/> (CA1001 não enxerga isso).
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Janela WPF: o recurso é liberado em OnClosed.")]
public partial class ProgressWindow : Window, IProgress<ProgressReport>
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Dispatcher _ui;
    private readonly string? _outputFileOrFolder;
    private readonly Action _flushReport;   // delegate criado uma vez só

    // Compartilhados entre trabalhador e UI (protegidos por _reportGate).
    private readonly object _reportGate = new();
    private ProgressReport _latestReport;
    private bool _flushScheduled;

    // Somente UI.
    private bool _completed;
    private bool _finalized;   // MarkFailed/MarkCompleted: relatórios atrasados não sobrescrevem o resultado

    // Capturado uma vez: continua utilizável pelo trabalhador depois que a janela fecha e a fonte é descartada.
    private readonly CancellationToken _token;

    public CancellationToken CancellationToken => _token;

    public ProgressWindow(string title, string? targetOutput = null)
    {
        UiTheme.Install(this);   // estilos e cores: antes do XAML, que usa DynamicResource
        InitializeComponent();
        Icon = AppIconRenderer.WindowIcon;
        _token = _cts.Token;
        _ui = Dispatcher;
        _flushReport = FlushReport;
        TxtTitle.Text = title;
        _outputFileOrFolder = targetOutput;
    }

    /// <summary>
    /// Fechar a janela no meio (Alt+F4, barra de tarefas) CANCELA a operação: antes ela seguia rodando
    /// invisível, sem como acompanhar nem cancelar. Libera o CancellationTokenSource.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        try
        {
            if (!_completed) _cts.Cancel();
        }
        catch (Exception ex) when (ex is AggregateException or ObjectDisposedException)
        {
            AppLog.Warning("Progress", "Falha ao cancelar a operação ao fechar a janela.", ex);
        }
        finally
        {
            _cts.Dispose();
        }

        base.OnClosed(e);
    }

    /// <summary>
    /// Recebe progresso de qualquer thread sem bloquear. Relatórios que chegam antes de a UI
    /// aplicar o anterior são coalescidos: vale sempre o mais recente.
    /// </summary>
    public void Report(ProgressReport value)
    {
        lock (_reportGate)
        {
            _latestReport = value;
            if (_flushScheduled) return;
            _flushScheduled = true;
        }

        _ = _ui.InvokeAsync(_flushReport);
    }

    /// <summary>Exibe a falha (ou o cancelamento). Pode ser chamado de qualquer thread; não bloqueia.</summary>
    public void MarkFailed(string errorMessage)
    {
        _ = _ui.InvokeAsync(() =>
        {
            _finalized = true;
            _completed = true;
            PrgBar.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "DangerBrush");
            TxtPercent.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "DangerBrush");
            IconGlyph.Text = "\uE783";   // ícone de erro
            IconDisc.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "DangerBrush");
            IconGlyph.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SurfaceBrush");
            TxtStatus.Text = errorMessage;
            TxtPercent.Text = Loc.Get("Progress.Error");
            BtnCancel.Content = Loc.Get("Common.Close");
            BtnCancel.IsEnabled = true;
        });
    }

    /// <summary>
    /// Resultado final de um lote: concluído (check) ou concluído com falhas parciais (atenção).
    /// Pode ser chamado de qualquer thread; um relatório de progresso atrasado não sobrescreve a mensagem.
    /// </summary>
    public void MarkCompleted(string message, bool warning = false)
    {
        _ = _ui.InvokeAsync(() =>
        {
            _finalized = true;
            _completed = true;
            PrgBar.Value = 100;
            TxtStatus.Text = message;
            PrgBar.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty,
                warning ? "WarningBrush" : "AccentBrush");
            TxtPercent.Text = warning ? Loc.Get("Progress.Warning") : "100%";
            TxtPercent.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                warning ? "WarningBrush" : "AccentTextBrush");
            IconGlyph.Text = warning ? "\uE7BA" : "\uE73E";
            IconDisc.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
                warning ? "WarningBrush" : "AccentBrush");
            IconGlyph.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                warning ? "WarningInkBrush" : "AccentInkBrush");
            BtnCancel.Content = Loc.Get("Common.Close");
            BtnCancel.IsEnabled = true;

            if (!string.IsNullOrEmpty(_outputFileOrFolder))
                BtnOpenFolder.Visibility = Visibility.Visible;
        });
    }

    private void FlushReport()
    {
        ProgressReport report;
        lock (_reportGate)
        {
            report = _latestReport;
            _flushScheduled = false;
        }

        if (_finalized) return;   // um relatório atrasado não sobrescreve o resultado final

        PrgBar.Value = report.Percentage;
        TxtPercent.Text = $"{report.Percentage:0}%";
        TxtStatus.Text = report.StatusText;

        if (report.Percentage >= 100.0)
        {
            _completed = true;
            IconGlyph.Text = "\uE73E";   // check
            BtnCancel.Content = Loc.Get("Common.Close");
            if (!string.IsNullOrEmpty(_outputFileOrFolder))
                BtnOpenFolder.Visibility = Visibility.Visible;
        }
    }

    /// <summary>A janela não tem moldura: arrastar pelo cartão move.</summary>
    private void Card_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase) return;
        try { DragMove(); } catch (InvalidOperationException) { /* botão solto no meio do clique */ }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_completed)
        {
            _cts.Cancel();
            TxtStatus.Text = Loc.Get("Progress.CancelRequested");
            BtnCancel.IsEnabled = false;
        }
        else
        {
            Close();
        }
    }

    /// <summary>
    /// Mostra o resultado no Explorer, sem elevação. O caminho passa por TrustedPath dentro de
    /// <see cref="UnelevatedLauncher.ShowInExplorer"/> (que nunca lança exceção).
    /// </summary>
    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_outputFileOrFolder is { Length: > 0 } target && !UnelevatedLauncher.ShowInExplorer(target))
            AppLog.Warning("Progress", $"Não foi possível abrir '{target}' no Explorer.");

        Close();
    }
}
