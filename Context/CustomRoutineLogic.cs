// ============================================================================
// RadialFlow - Rotinas nativas
// ----------------------------------------------------------------------------
// Uma rotina é uma sequência curta de ações NATIVAS do RadialFlow e pausas.
// A lista de ações é fechada (sem shell, argumentos, URLs ou texto) e cada
// passo é revalidado no contexto no momento da execução, pela mesma
// ActionRouter.Logic usada pelas fatias automáticas.
//
// A execução é sequencial: uma pausa termina antes do próximo passo e uma ação
// que o contexto não permite interrompe a rotina, em vez de pular para a
// seguinte. Rotinas são canceláveis; disparar outra rotina cancela a anterior.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RadialFlow.Config;

namespace RadialFlow.Context;

/// <summary>Passo já validado: uma ação permitida ou uma pausa.</summary>
internal abstract record CustomRoutineStepPlan;

internal sealed record CustomRoutineActionPlan(RadialAction Action) : CustomRoutineStepPlan;

internal sealed record CustomRoutineDelayPlan(int Milliseconds) : CustomRoutineStepPlan;

internal sealed record CustomRoutinePlan(IReadOnlyList<CustomRoutineStepPlan> Steps);

/// <summary>Resultado da execução; <see cref="Completed"/> falso quando um passo recusou o contexto.</summary>
internal readonly record struct CustomRoutineRunResult(bool Completed, int ExecutedActions, int ExecutedSteps);

internal static class CustomRoutineLogic
{
    internal const int MaxSteps = 8;
    internal const int MinDelayMilliseconds = 50;
    internal const int MaxDelayMilliseconds = 2000;
    internal const int MaxTotalDelayMilliseconds = 5000;

    // Lista fechada: só ações sem diálogo, sem entrada do usuário e sem efeito destrutivo
    // irreversível. Converter/Comprimir abrem progresso assíncrono; Renomear/Mover pedem
    // entrada; Fechar/Bloquear encerram janela/sessão; Limpar apaga conteúdo.
    private static readonly HashSet<RadialAction> AllowedActions =
    [
        RadialAction.CopyPath,
        RadialAction.SendTo,
        RadialAction.SnapLeft,
        RadialAction.SnapRight,
        RadialAction.Maximize,
        RadialAction.Minimize,
        RadialAction.AlwaysOnTop,
        RadialAction.Transparency,
        RadialAction.Paste,
        RadialAction.Format,
        RadialAction.Translate,
        RadialAction.SearchWeb,
        RadialAction.ClipboardHistory
    ];

    // Abrem outra janela/seletor e mudam o foco: só podem encerrar a rotina, senão
    // um Paste/Format seguinte poderia ir para a superfície errada.
    private static readonly HashSet<RadialAction> FocusChangingActions =
    [
        RadialAction.SearchWeb,
        RadialAction.Translate,
        RadialAction.ClipboardHistory
    ];

    internal static bool IsAllowedAction(RadialAction action) => AllowedActions.Contains(action);

    internal static bool IsFocusChangingAction(RadialAction action) => FocusChangingActions.Contains(action);

    /// <summary>Primeiro passo que muda o foco e não é o último, se houver.</summary>
    internal static bool TryFindMisplacedFocusStep(
        IReadOnlyList<CustomRoutineStep> steps, out int index, out string? reason)
    {
        index = -1;
        reason = null;

        for (int i = 0; i < steps.Count - 1; i++)
        {
            CustomRoutineStep step = steps[i];
            if (step is not null &&
                step.Kind == CustomRoutineStepKind.Action &&
                Enum.IsDefined(step.Action) &&
                IsFocusChangingAction(step.Action))
            {
                index = i;
                reason = $"A ação '{step.Action}' abre outra janela e só pode ser o último passo.";
                return true;
            }
        }

        return false;
    }

    /// <summary>Valida a rotina inteira sem tocar no contexto nem em disco.</summary>
    internal static bool TryBuildPlan(
        CustomMenuItem item, out CustomRoutinePlan plan, out string? reason)
    {
        plan = null!;
        reason = null;

        if (item is null) { reason = "O item está vazio."; return false; }
        if (item.Kind != CustomMenuItemKind.Routine) { reason = "O item não é uma rotina."; return false; }

        if (item.RoutineVersion != CustomMenuItem.RoutineSchemaVersion)
        {
            reason = $"A versão de rotina {item.RoutineVersion} não é suportada.";
            return false;
        }

        var steps = item.Steps ?? new List<CustomRoutineStep>();
        if (steps.Count == 0) { reason = "A rotina não tem passos."; return false; }
        if (steps.Count > MaxSteps) { reason = $"A rotina passa de {MaxSteps} passos."; return false; }

        var planned = new List<CustomRoutineStepPlan>(steps.Count);
        int totalDelay = 0;
        bool hasAction = false;

        for (int i = 0; i < steps.Count; i++)
        {
            CustomRoutineStep step = steps[i];
            if (!TryValidateStep(step, out string? stepReason))
            {
                reason = $"Passo {i + 1}: {stepReason}";
                return false;
            }

            if (step.Kind == CustomRoutineStepKind.Delay)
            {
                totalDelay += step.DelayMilliseconds;
                planned.Add(new CustomRoutineDelayPlan(step.DelayMilliseconds));
            }
            else
            {
                hasAction = true;
                planned.Add(new CustomRoutineActionPlan(step.Action));
            }
        }

        if (!hasAction) { reason = "A rotina precisa de pelo menos uma ação."; return false; }
        if (totalDelay > MaxTotalDelayMilliseconds)
        {
            reason = $"As pausas somam mais de {MaxTotalDelayMilliseconds} ms.";
            return false;
        }
        if (TryFindMisplacedFocusStep(steps, out int focusIndex, out string? focusReason))
        {
            reason = $"Passo {focusIndex + 1}: {focusReason}";
            return false;
        }

        plan = new CustomRoutinePlan(planned);
        return true;
    }

    /// <summary>Valida um passo isolado (o diálogo usa isto para apontar o passo errado).</summary>
    internal static bool TryValidateStep(CustomRoutineStep? step, out string? reason)
    {
        reason = null;

        if (step is null) { reason = "O passo está vazio."; return false; }

        switch (step.Kind)
        {
            case CustomRoutineStepKind.Action:
                if (!Enum.IsDefined(step.Action))
                {
                    reason = $"A ação desconhecida ({(int)step.Action}) não existe.";
                    return false;
                }
                if (!IsAllowedAction(step.Action))
                {
                    reason = $"A ação '{step.Action}' não é permitida em rotinas.";
                    return false;
                }
                return true;

            case CustomRoutineStepKind.Delay:
                if (step.DelayMilliseconds < MinDelayMilliseconds)
                {
                    reason = $"A pausa precisa ter pelo menos {MinDelayMilliseconds} ms.";
                    return false;
                }
                if (step.DelayMilliseconds > MaxDelayMilliseconds)
                {
                    reason = $"A pausa passa de {MaxDelayMilliseconds} ms.";
                    return false;
                }
                return true;

            default:
                reason = $"O tipo de passo ({(int)step.Kind}) é inválido.";
                return false;
        }
    }

    /// <summary>
    /// Executa os passos em ordem. <paramref name="runStep"/> devolve false quando o contexto
    /// recusa a ação; nesse caso a rotina para. Cancelamento propaga <see cref="OperationCanceledException"/>.
    /// </summary>
    internal static async Task<CustomRoutineRunResult> ExecuteAsync(
        CustomRoutinePlan plan,
        ContextInfo context,
        Func<RadialAction, ContextInfo, CancellationToken, Task<bool>> runStep,
        Func<int, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        int executed = 0;
        int executedSteps = 0;

        foreach (CustomRoutineStepPlan step in plan.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (step)
            {
                case CustomRoutineDelayPlan pause:
                    await delay(pause.Milliseconds, cancellationToken).ConfigureAwait(false);
                    executedSteps++;
                    break;

                case CustomRoutineActionPlan action:
                    if (!await runStep(action.Action, context, cancellationToken).ConfigureAwait(false))
                        return new CustomRoutineRunResult(false, executed, executedSteps);
                    executed++;
                    executedSteps++;
                    break;
            }
        }

        return new CustomRoutineRunResult(true, executed, executedSteps);
    }
}
