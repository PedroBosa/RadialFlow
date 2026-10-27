using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RadialFlow.Config;
using RadialFlow.Context;
using Xunit;

namespace RadialFlow.Tests;

public sealed class CustomRoutineLogicTests
{
    private static CustomRoutineStep Action(RadialAction action) => new()
    {
        Kind = CustomRoutineStepKind.Action,
        Action = action,
    };

    private static CustomRoutineStep Delay(int milliseconds) => new()
    {
        Kind = CustomRoutineStepKind.Delay,
        DelayMilliseconds = milliseconds,
    };

    private static CustomMenuItem Routine(params CustomRoutineStep[] steps) => new()
    {
        Label = "Rotina",
        Kind = CustomMenuItemKind.Routine,
        Steps = steps.ToList(),
    };

    private static string? Reason(CustomMenuItem item) =>
        CustomRoutineLogic.TryBuildPlan(item, out _, out string? reason) ? null : reason;

    private static CustomRoutinePlan Plan(CustomMenuItem item)
    {
        Assert.True(CustomRoutineLogic.TryBuildPlan(item, out CustomRoutinePlan plan, out string? reason), reason);
        return plan;
    }

    private static ContextInfo Context() =>
        new(RadialContextKind.FileOrFolder, (IntPtr)0x1234, "explorer", 10, 20, ContextInfo.NoPaths);

    // ------------------------------------------------------------------
    // Validação
    // ------------------------------------------------------------------

    [Fact]
    public void TryBuildPlan_PreservaOrdemEAcoesPermitidas()
    {
        CustomRoutinePlan plan = Plan(Routine(
            Action(RadialAction.CopyPath),
            Delay(250),
            Action(RadialAction.Paste)));

        Assert.Collection(plan.Steps,
            step => Assert.Equal(RadialAction.CopyPath, Assert.IsType<CustomRoutineActionPlan>(step).Action),
            step => Assert.Equal(250, Assert.IsType<CustomRoutineDelayPlan>(step).Milliseconds),
            step => Assert.Equal(RadialAction.Paste, Assert.IsType<CustomRoutineActionPlan>(step).Action));
    }

    [Theory]
    [InlineData(RadialAction.Convert)]
    [InlineData(RadialAction.Compress)]
    [InlineData(RadialAction.Rename)]
    [InlineData(RadialAction.MoveTo)]
    [InlineData(RadialAction.Clear)]
    [InlineData(RadialAction.Close)]
    [InlineData(RadialAction.LockScreen)]
    [InlineData(RadialAction.FavoriteApps)]
    [InlineData(RadialAction.SystemControls)]
    public void TryBuildPlan_RecusaAcoesForaDaLista(RadialAction action)
    {
        string? reason = Reason(Routine(Action(action)));

        Assert.NotNull(reason);
        Assert.Contains("não é permitida", reason);
    }

    [Fact]
    public void TryBuildPlan_RecusaAcaoDesconhecida()
    {
        string? reason = Reason(Routine(Action((RadialAction)999)));

        Assert.NotNull(reason);
        Assert.Contains("desconhecida", reason);
    }

    [Fact]
    public void TryBuildPlan_RecusaLimitesEPassosVazios()
    {
        Assert.Contains("não tem passos", Reason(Routine()));

        CustomRoutineStep[] tooMany = Enumerable.Range(0, CustomRoutineLogic.MaxSteps + 1)
            .Select(_ => Action(RadialAction.CopyPath))
            .ToArray();
        Assert.Contains("passa de 8", Reason(Routine(tooMany)));

        Assert.Contains("pelo menos uma ação", Reason(Routine(Delay(100))));
        Assert.Contains("pelo menos 50", Reason(Routine(Action(RadialAction.CopyPath), Delay(49))));
        Assert.Contains("passa de 2000", Reason(Routine(Action(RadialAction.CopyPath), Delay(2001))));
        Assert.Contains("somam mais de 5000", Reason(Routine(
            Action(RadialAction.CopyPath), Delay(2000), Delay(2000), Delay(1500))));

        string? nullStep = Reason(Routine((CustomRoutineStep)null!));
        Assert.NotNull(nullStep);
        Assert.Contains("Passo 1", nullStep);
    }

    [Fact]
    public void TryBuildPlan_RecusaVersaoDesconhecida()
    {
        CustomMenuItem item = Routine(Action(RadialAction.CopyPath));
        item.RoutineVersion = CustomMenuItem.RoutineSchemaVersion + 1;

        Assert.Contains("não é suportada", Reason(item));
    }

    [Fact]
    public void TryBuildPlan_AceitaPausaTotalNoLimite()
    {
        CustomRoutinePlan plan = Plan(Routine(
            Action(RadialAction.CopyPath), Delay(2000), Delay(2000), Delay(1000)));

        Assert.Equal(4, plan.Steps.Count);
    }

    [Theory]
    [InlineData(RadialAction.SearchWeb)]
    [InlineData(RadialAction.Translate)]
    [InlineData(RadialAction.ClipboardHistory)]
    public void TryBuildPlan_AcoesQueAbremOutraJanela_SoPodemSerOUltimoPasso(RadialAction action)
    {
        CustomMenuItem misplaced = Routine(Action(action), Action(RadialAction.CopyPath));
        string? reason = Reason(misplaced);

        Assert.NotNull(reason);
        Assert.Contains("só pode ser o último passo", reason);
        Assert.True(CustomRoutineLogic.TryFindMisplacedFocusStep(misplaced.Steps, out int index, out _));
        Assert.Equal(0, index);

        Assert.True(CustomRoutineLogic.TryBuildPlan(
            Routine(Action(RadialAction.CopyPath), Action(action)), out _, out _));
    }

    // ------------------------------------------------------------------
    // Execução
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ExecutaNaOrdemEParaNaPrimeiraFalha()
    {
        CustomRoutinePlan plan = Plan(Routine(
            Action(RadialAction.CopyPath),
            Delay(120),
            Action(RadialAction.Paste),
            Action(RadialAction.Maximize)));

        var calls = new List<string>();
        CustomRoutineRunResult result = await CustomRoutineLogic.ExecuteAsync(
            plan,
            Context(),
            (action, _, _) =>
            {
                calls.Add(action.ToString());
                return Task.FromResult(action != RadialAction.Paste);
            },
            (milliseconds, _) =>
            {
                calls.Add($"delay:{milliseconds}");
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Equal(1, result.ExecutedActions);
        Assert.Equal(2, result.ExecutedSteps);
        Assert.Equal(new[] { "CopyPath", "delay:120", "Paste" }, calls);
    }

    [Fact]
    public async Task ExecuteAsync_ConcluiQuandoTodosOsPassosPassam()
    {
        CustomRoutinePlan plan = Plan(Routine(
            Action(RadialAction.CopyPath),
            Delay(100),
            Action(RadialAction.Paste)));

        var actions = new List<RadialAction>();
        CustomRoutineRunResult result = await CustomRoutineLogic.ExecuteAsync(
            plan,
            Context(),
            (action, _, _) => { actions.Add(action); return Task.FromResult(true); },
            (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(2, result.ExecutedActions);
        Assert.Equal(3, result.ExecutedSteps);
        Assert.Equal(new[] { RadialAction.CopyPath, RadialAction.Paste }, actions);
    }

    [Fact]
    public async Task ExecuteAsync_CanceladaDuranteAPausa_NaoExecutaAProximaAcao()
    {
        CustomRoutinePlan plan = Plan(Routine(
            Action(RadialAction.CopyPath),
            Delay(2000),
            Action(RadialAction.Paste)));
        var calls = new List<string>();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CustomRoutineLogic.ExecuteAsync(
                plan,
                Context(),
                (action, _, _) =>
                {
                    calls.Add(action.ToString());
                    return Task.FromResult(true);
                },
                (milliseconds, token) =>
                {
                    calls.Add($"delay:{milliseconds}");
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
                cancellation.Token));

        Assert.Equal(new[] { "CopyPath", "delay:2000" }, calls);
    }

    [Fact]
    public async Task ExecuteAsync_Cancelada_NaoExecutaNenhumPasso()
    {
        CustomRoutinePlan plan = Plan(Routine(Action(RadialAction.CopyPath)));
        int calls = 0;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CustomRoutineLogic.ExecuteAsync(
                plan,
                Context(),
                (_, _, _) => { calls++; return Task.FromResult(true); },
                (_, _) => { calls++; return Task.CompletedTask; },
                cancellation.Token));

        Assert.Equal(0, calls);
    }
}
