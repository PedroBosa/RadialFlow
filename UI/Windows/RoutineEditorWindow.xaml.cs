// ============================================================================
// RadialFlow - Diálogo de passos da rotina
// ----------------------------------------------------------------------------
// Edita a sequência de ações nativas e pausas de um item do tipo Rotina.
// Trabalha numa cópia: só "Concluir" devolve os passos ao editor da aba Menus,
// que ainda passa pela validação e pelo normalizador antes de salvar.
//
// A lista de ações vem de CustomMenuChoices.RoutineActions, derivada da mesma
// lista fechada usada pela execução. Não há campo livre: nem shell, nem
// argumentos, nem URL, nem texto.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RadialFlow.Config;
using RadialFlow.Context;

namespace RadialFlow.UI;

/// <summary>Linha da lista de passos (glifo e rótulo são calculados do passo).</summary>
public sealed class RoutineStepRow
{
    internal RoutineStepRow(CustomRoutineStep step) => Step = step;

    internal CustomRoutineStep Step { get; }

    public string Glyph => Step.Kind == CustomRoutineStepKind.Delay ? "\uE916" : "\uE768";

    public string Label => Step.Kind == CustomRoutineStepKind.Delay
        ? $"Aguardar {Step.DelayMilliseconds} ms"
        : CustomMenuChoices.ActionLabel(Step.Action);
}

public partial class RoutineEditorWindow : Window
{
    private static readonly int[] DelayChoices = [50, 100, 200, 250, 500, 750, 1000, 1500, 2000];

    private readonly ObservableCollection<RoutineStepRow> _rows = new();
    private readonly List<CustomRoutineStep> _steps = new();
    private RoutineStepRow? _selected;
    private bool _loading;

    /// <summary>Só para testes: chamado quando a janela termina de renderizar, antes de ShowDialog bloquear.</summary>
    internal static Action<RoutineEditorWindow>? BeforeShowForTests { get; set; }

    internal RoutineEditorWindow(string itemLabel, IEnumerable<CustomRoutineStep> steps)
    {
        UiTheme.Install(this);   // estilos e cores: antes do XAML, que usa DynamicResource
        UiTheme.StyleTitleBar(this);
        InitializeComponent();
        Icon = AppIconRenderer.WindowIcon;

        string label = string.IsNullOrWhiteSpace(itemLabel) ? "esta rotina" : itemLabel.Trim();
        TxtSubtitle.Text = $"“{label}” executa os passos abaixo, nesta ordem.";

        foreach (CustomRoutineStep step in steps ?? Array.Empty<CustomRoutineStep>())
            if (step is not null) _steps.Add(Clone(step));

        ListSteps.ItemsSource = _rows;

        foreach (var (action, actionLabel) in CustomMenuChoices.RoutineActions)
            CmbStepAction.Items.Add(new ComboBoxItem { Content = actionLabel, Tag = action });

        foreach (int delay in DelayChoices)
            CmbStepDelay.Items.Add(new ComboBoxItem { Content = $"{delay} ms", Tag = delay });

        RebuildRows(0);
        UpdateState();
    }

    /// <summary>Passos editados, na ordem de execução. Válido apenas quando o diálogo devolve true.</summary>
    internal IReadOnlyList<CustomRoutineStep> Steps => _steps.Select(Clone).ToList();

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        BeforeShowForTests?.Invoke(this);
    }

    // ------------------------------------------------------------------
    // Lista
    // ------------------------------------------------------------------

    private void ListSteps_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        _loading = true;
        try
        {
            LoadStepEditor(ListSteps.SelectedItem as RoutineStepRow);
        }
        finally
        {
            _loading = false;
        }

        UpdateState();
    }

    private void LoadStepEditor(RoutineStepRow? row)
    {
        _selected = row;
        BdStepEditor.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        PnlNoStepSelected.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;

        if (row is null)
        {
            TxtStepKind.Text = "";
            CmbStepAction.SelectedIndex = -1;
            CmbStepDelay.SelectedIndex = -1;
            SetPanels(CustomRoutineStepKind.Action);
            return;
        }

        CustomRoutineStep step = row.Step;
        SetPanels(step.Kind);
        TxtStepKind.Text = step.Kind == CustomRoutineStepKind.Delay
            ? "Pausa entre ações"
            : "Ação do RadialFlow";

        if (step.Kind == CustomRoutineStepKind.Delay) SelectDelay(step.DelayMilliseconds);
        else SelectAction(step.Action);
    }

    private void BtnAddAction_Click(object sender, RoutedEventArgs e) =>
        AddStep(new CustomRoutineStep
        {
            Kind = CustomRoutineStepKind.Action,
            Action = RadialAction.CopyPath,
        });

    private void BtnAddDelay_Click(object sender, RoutedEventArgs e) =>
        AddStep(new CustomRoutineStep
        {
            Kind = CustomRoutineStepKind.Delay,
            DelayMilliseconds = 250,
        });

    private void AddStep(CustomRoutineStep step)
    {
        if (_steps.Count >= CustomRoutineLogic.MaxSteps)
        {
            Dialogs.Message(this, "Passos da rotina",
                $"Uma rotina pode ter no máximo {CustomRoutineLogic.MaxSteps} passos.", DialogIcon.Warning);
            return;
        }

        _steps.Add(step);
        RebuildRows(_steps.Count - 1);
        UpdateState();
    }

    private void BtnRemoveStep_Click(object sender, RoutedEventArgs e)
    {
        int index = ListSteps.SelectedIndex;
        if (index < 0 || index >= _steps.Count) return;

        _steps.RemoveAt(index);
        RebuildRows(_steps.Count == 0 ? -1 : Math.Min(index, _steps.Count - 1));
        UpdateState();
    }

    private void BtnStepUp_Click(object sender, RoutedEventArgs e) => MoveStep(-1);

    private void BtnStepDown_Click(object sender, RoutedEventArgs e) => MoveStep(+1);

    private void MoveStep(int delta)
    {
        int index = ListSteps.SelectedIndex;
        int target = index + delta;
        if (index < 0 || target < 0 || target >= _steps.Count) return;

        CustomRoutineStep step = _steps[index];
        _steps.RemoveAt(index);
        _steps.Insert(target, step);
        RebuildRows(target);
        UpdateState();
    }

    private void RebuildRows(int selectIndex)
    {
        bool previous = _loading;
        _loading = true;
        try
        {
            _rows.Clear();
            foreach (CustomRoutineStep step in _steps)
                _rows.Add(new RoutineStepRow(step));

            if (_rows.Count == 0)
            {
                ListSteps.SelectedIndex = -1;
                LoadStepEditor(null);
            }
            else
            {
                int index = selectIndex < 0 ? 0 : Math.Min(selectIndex, _rows.Count - 1);
                ListSteps.SelectedIndex = index;
                LoadStepEditor(_rows[index]);
            }
        }
        finally
        {
            _loading = previous;
        }
    }

    // ------------------------------------------------------------------
    // Campos do passo
    // ------------------------------------------------------------------

    private void StepField_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _selected is null) return;

        if (_selected.Step.Kind == CustomRoutineStepKind.Delay)
        {
            if (CmbStepDelay.SelectedItem is ComboBoxItem { Tag: int milliseconds })
                _selected.Step.DelayMilliseconds = milliseconds;
        }
        else if (CmbStepAction.SelectedItem is ComboBoxItem { Tag: RadialAction action })
        {
            _selected.Step.Action = action;
        }

        RefreshSelectedRow();
    }

    private void RefreshSelectedRow()
    {
        if (_selected is null) return;

        bool previous = _loading;
        _loading = true;
        try
        {
            ListSteps.Items.Refresh();
            ListSteps.SelectedItem = _selected;
        }
        finally
        {
            _loading = previous;
        }

        UpdateState();
    }

    private void SetPanels(CustomRoutineStepKind kind)
    {
        bool delay = kind == CustomRoutineStepKind.Delay;
        PnlStepAction.Visibility = delay ? Visibility.Collapsed : Visibility.Visible;
        PnlStepDelay.Visibility = delay ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateState()
    {
        int totalDelay = _steps
            .Where(s => s.Kind == CustomRoutineStepKind.Delay)
            .Sum(s => s.DelayMilliseconds);
        bool hasAction = _steps.Any(s => s.Kind == CustomRoutineStepKind.Action);

        TxtStepCount.Text = _steps.Count == 0
            ? $"0 de {CustomRoutineLogic.MaxSteps} passos"
            : $"{_steps.Count} de {CustomRoutineLogic.MaxSteps} passos · {totalDelay} ms de pausa";
        PnlNoSteps.Visibility = _steps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        bool hasSelection = _selected is not null;
        int index = ListSteps.SelectedIndex;
        BtnAddAction.IsEnabled = _steps.Count < CustomRoutineLogic.MaxSteps;
        BtnAddDelay.IsEnabled = _steps.Count < CustomRoutineLogic.MaxSteps;
        BtnRemoveStep.IsEnabled = hasSelection;
        BtnStepUp.IsEnabled = hasSelection && index > 0;
        BtnStepDown.IsEnabled = hasSelection && index >= 0 && index < _steps.Count - 1;

        string? warning = null;
        if (_steps.Count > 0 && !hasAction)
        {
            warning = "Adicione pelo menos uma ação: uma rotina só com pausas não faz nada.";
        }
        else if (totalDelay > CustomRoutineLogic.MaxTotalDelayMilliseconds)
        {
            warning = $"As pausas somam {totalDelay} ms; o máximo é {CustomRoutineLogic.MaxTotalDelayMilliseconds} ms.";
        }
        else if (_selected is not null &&
                 !CustomRoutineLogic.TryValidateStep(_selected.Step, out string? stepReason))
        {
            warning = stepReason;
        }
        else if (CustomRoutineLogic.TryFindMisplacedFocusStep(_steps, out _, out string? focusReason))
        {
            warning = focusReason;
        }

        TxtStepsWarning.Text = warning ?? "";
        TxtStepsWarning.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------------
    // Concluir
    // ------------------------------------------------------------------

    private void BtnDone_Click(object sender, RoutedEventArgs e)
    {
        string? problem = FirstProblem();
        if (problem is not null)
        {
            Dialogs.Message(this, "Passos da rotina", problem, DialogIcon.Warning);
            return;
        }

        DialogResult = true;
    }

    /// <summary>Seleciona o primeiro passo inválido antes de devolver o motivo.</summary>
    private string? FirstProblem()
    {
        if (_steps.Count == 0)
            return "Adicione pelo menos uma ação à rotina.";
        if (_steps.All(s => s.Kind != CustomRoutineStepKind.Action))
            return "Adicione pelo menos uma ação: uma rotina só com pausas não faz nada.";
        if (_steps.Count > CustomRoutineLogic.MaxSteps)
            return $"Uma rotina pode ter no máximo {CustomRoutineLogic.MaxSteps} passos.";

        int totalDelay = _steps
            .Where(s => s.Kind == CustomRoutineStepKind.Delay)
            .Sum(s => s.DelayMilliseconds);
        if (totalDelay > CustomRoutineLogic.MaxTotalDelayMilliseconds)
            return $"As pausas somam mais de {CustomRoutineLogic.MaxTotalDelayMilliseconds} ms.";

        foreach (CustomRoutineStep step in _steps)
        {
            if (CustomRoutineLogic.TryValidateStep(step, out string? reason)) continue;

            int index = _steps.IndexOf(step);
            _loading = true;
            try
            {
                ListSteps.SelectedIndex = index;
                LoadStepEditor(_rows[index]);
            }
            finally
            {
                _loading = false;
            }

            return reason;
        }

        if (CustomRoutineLogic.TryFindMisplacedFocusStep(_steps, out int focusIndex, out string? focusReason))
        {
            _loading = true;
            try
            {
                ListSteps.SelectedIndex = focusIndex;
                LoadStepEditor(_rows[focusIndex]);
            }
            finally
            {
                _loading = false;
            }

            return focusReason;
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Utilitários
    // ------------------------------------------------------------------

    private static CustomRoutineStep Clone(CustomRoutineStep step) => new()
    {
        Kind = step.Kind,
        Action = step.Action,
        DelayMilliseconds = step.DelayMilliseconds,
    };

    private void SelectAction(RadialAction action)
    {
        foreach (ComboBoxItem entry in CmbStepAction.Items)
        {
            if (entry.Tag is RadialAction candidate && candidate == action)
            {
                CmbStepAction.SelectedItem = entry;
                return;
            }
        }

        // Ação vinda de um arquivo editado à mão: mostra o valor atual (o Concluir recusa).
        var custom = new ComboBoxItem { Content = CustomMenuChoices.ActionLabel(action), Tag = action };
        CmbStepAction.Items.Add(custom);
        CmbStepAction.SelectedItem = custom;
    }

    private void SelectDelay(int delay)
    {
        foreach (ComboBoxItem entry in CmbStepDelay.Items)
        {
            if (entry.Tag is int candidate && candidate == delay)
            {
                CmbStepDelay.SelectedItem = entry;
                return;
            }
        }

        var custom = new ComboBoxItem { Content = $"{delay} ms", Tag = delay };
        CmbStepDelay.Items.Add(custom);
        CmbStepDelay.SelectedItem = custom;
    }
}
