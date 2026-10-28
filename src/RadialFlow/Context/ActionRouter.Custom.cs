// ============================================================================
// RadialFlow - Execução dos itens de menu personalizados
// ----------------------------------------------------------------------------
// Só é chamado quando o usuário escolhe uma fatia personalizada. Valida ANTES
// de qualquer efeito e roda em background, como as ações nativas.
//
// Launch/OpenUrl/OpenPath saem pelo UnelevatedLauncher (processo-filho sem
// elevação). Hotkey/Text restauram o foco na janela de origem via TextTarget e
// nunca enviam teclas para outro aplicativo.
// ============================================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RadialFlow.Config;
using RadialFlow.Infrastructure;
using RadialFlow.Services;

namespace RadialFlow.Context;

public static partial class ActionRouter
{
    private static readonly object _routineGate = new();
    private static CancellationTokenSource? _routineCancellation;

    /// <summary>Ponto de entrada das fatias personalizadas. Nunca lança.</summary>
    public static void RunCustom(CustomMenuItem item, ContextInfo ctx)
    {
        if (item is null || !item.Enabled) return;

        if (item.Kind == CustomMenuItemKind.Action)
        {
            Run(item.Action, ctx);
            return;
        }

        if (item.Kind == CustomMenuItemKind.Routine)
        {
            _ = Task.Run(() => RunRoutineAsync(item, ctx));
            return;
        }

        if (!CustomActionLogic.TryValidate(item, out CustomActionPlan plan, out string? reason))
        {
            AppLog.Warning("Menus", $"'{item.Label}' recusado: {reason}");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await DispatchCustomAsync(plan, ctx).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error("Menus", $"Erro em '{item.Label}'", ex);
            }
        });
    }

    /// <summary>
    /// Valida a rotina e assume o lugar de qualquer rotina em andamento. Ferramentas
    /// nativas não abrem janela de progresso: ela tiraria o foco necessário para
    /// Paste/Format e mudaria o alvo da rotina.
    /// </summary>
    private static async Task RunRoutineAsync(CustomMenuItem item, ContextInfo ctx)
    {
        if (!CustomRoutineLogic.TryBuildPlan(item, out CustomRoutinePlan plan, out string? planReason))
        {
            AppLog.Warning("Menus", $"Rotina '{item.Label}' recusada: {planReason}");
            return;
        }

        var cancellation = new CancellationTokenSource();
        lock (_routineGate)
        {
            _routineCancellation?.Cancel();
            _routineCancellation = cancellation;
        }

        try
        {
            CustomRoutineRunResult result = await CustomRoutineLogic.ExecuteAsync(
                plan, ctx, RunRoutineStepAsync, Task.Delay, cancellation.Token).ConfigureAwait(false);

            if (result.Completed)
                AppLog.Debug("Menus", $"Rotina '{item.Label}' concluída ({result.ExecutedActions} ação(ões)).");
            else
                AppLog.Warning("Menus", $"Rotina '{item.Label}' interrompida no passo {result.ExecutedSteps + 1}.");
        }
        catch (OperationCanceledException)
        {
            AppLog.Debug("Menus", $"Rotina '{item.Label}' cancelada.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Menus", $"Erro na rotina '{item.Label}'", ex);
        }
        finally
        {
            lock (_routineGate)
            {
                if (ReferenceEquals(_routineCancellation, cancellation)) _routineCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private static async Task<bool> RunRoutineStepAsync(
        RadialAction action, ContextInfo ctx, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Logic.TryValidate(action, ctx, out ContextInfo safeCtx, out string? reason))
        {
            AppLog.Warning("Menus", $"Rotina: {action} recusada: {reason}");
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await DispatchAsync(action, safeCtx).ConfigureAwait(false);
        return true;
    }

    private static async Task DispatchCustomAsync(CustomActionPlan plan, ContextInfo ctx)
    {
        switch (plan.Kind)
        {
            case CustomMenuItemKind.Launch:
            case CustomMenuItemKind.OpenUrl:
            case CustomMenuItemKind.OpenPath:
                if (!UnelevatedLauncher.Start(plan.Target))
                    AppLog.Warning("Menus", $"Não foi possível abrir '{plan.Target}'.");
                break;

            case CustomMenuItemKind.Hotkey:
                if (!TextTarget.Prepare(ctx, strict: false).Ready)
                {
                    AppLog.Debug("Menus", "Atalho cancelado: a janela de origem não está em foco.");
                    break;
                }
                SimulateKeys(plan.Keys);
                AppLog.Debug("Menus", $"Atalho '{plan.Target}'.");
                break;

            case CustomMenuItemKind.Text:
                PasteCustomText(plan.Text, ctx);
                break;

            case CustomMenuItemKind.TextTransform:
                await TransformSelectedTextAsync(plan, ctx).ConfigureAwait(false);
                break;

            default:
                AppLog.Warning("Menus", $"Tipo de item desconhecido: {plan.Kind}");
                break;
        }
    }

    /// <summary>Cola o texto e tenta devolver o conteúdo anterior do clipboard.</summary>
    private static void PasteCustomText(string text, ContextInfo ctx)
    {
        if (!TextTarget.Prepare(ctx, strict: false).Ready)
        {
            AppLog.Debug("Menus", "Texto cancelado: a janela de origem não está em foco.");
            return;
        }

        string? previousText = _ui.Invoke(() =>
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch { return null; }
        });

        bool copied = _ui.Invoke(() =>
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warning("Menus", $"Área de transferência indisponível: {ex.Message}");
                return false;
            }
        });

        if (!copied) return;

        SimulateKeys(VK_CONTROL, VK_V);
        Thread.Sleep(150);   // dá tempo de o app de destino consumir o Ctrl+V

        _ui.Invoke(() =>
        {
            try
            {
                if (previousText is not null) Clipboard.SetText(previousText);
                else Clipboard.Clear();
            }
            catch { /* melhor esforço: o texto já foi colado */ }
        });

        AppLog.Debug("Menus", "Texto colado.");
    }

    /// <summary>
    /// Captura a seleção (Ctrl+C), transforma localmente e cola por cima. O clipboard anterior é
    /// restaurado pela captura e novamente pela colagem; texto vazio ou inalterado não mexe no alvo.
    /// </summary>
    private static async Task TransformSelectedTextAsync(CustomActionPlan plan, ContextInfo ctx)
    {
        string? text = await CaptureSelectedTextAsync(ctx).ConfigureAwait(false);
        if (string.IsNullOrEmpty(text))
        {
            AppLog.Debug("Menus", $"Transformação {plan.Transform}: seleção vazia ou indisponível.");
            return;
        }

        string transformed = TextTransforms.Apply(text, plan.Transform);
        if (transformed.Length == 0 || string.Equals(transformed, text, StringComparison.Ordinal))
        {
            AppLog.Debug("Menus", $"Transformação {plan.Transform}: nada a alterar.");
            return;
        }

        if (transformed.Length > CustomMenuNormalizer.MaxTextLength)
        {
            AppLog.Warning("Menus",
                $"Transformação {plan.Transform} ignorada: resultado acima de {CustomMenuNormalizer.MaxTextLength} caracteres.");
            return;
        }

        PasteCustomText(transformed, ctx);
        AppLog.Debug("Menus",
            $"Transformação {plan.Transform} aplicada ({text.Length} → {transformed.Length} caracteres).");
    }
}
