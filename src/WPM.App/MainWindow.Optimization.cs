using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WPM.App.Optimization;

namespace WPM.App;

public partial class MainWindow
{
    private readonly SessionOptimizer optimizer = new();
    private OptimizationResource currentResource;
    private OptimizationChoice[] choices = [];

    private void OptimizationCards_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            OptimizationCards.Tag = e.NewSize.Width >= 840 ? 4 : 2;
        }
    }

    private async void VerifySystemButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy)
        {
            return;
        }

        ClearPlanActions();
        OptimizationCards.ItemsSource = null;
        SampleTimeText.Text = "";
        OptimizationResultPanel.Visibility = Visibility.Collapsed;
        await RunOperationAsync("Medindo CPU, RAM, disco e GPU…", "Não foi possível concluir a verificação de desempenho.", async () =>
        {
            ShowPerformanceSample(await Task.Run(OptimizationSampler.Collect));
            SetStatus("Verificação concluída. Confira as medições e os processos por recurso.");
        });
    }

    private void OpenResourcePlan_Click(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button { DataContext: OptimizationCard { HasActions: true } card })
        {
            return;
        }

        ClearPlanActions();
        OptimizationResultPanel.Visibility = Visibility.Collapsed;
        currentResource = card.Resource;
        choices = card.Actions.Select(action => new OptimizationChoice(action)).ToArray();
        PlanObjective.Text = $"Otimização · {card.Title}";
        PlanEvidence.Text = $"{card.Value} · {card.Detail}";
        PlanActions.ItemsSource = choices;
        PlanPanel.Visibility = Visibility.Visible;
        ApplyPlanButton.Visibility = Visibility.Visible;
        SetStatus("Selecione as ações que deseja aplicar.");
        Dispatcher.InvokeAsync(() =>
        {
            if (PlanPanel.Visibility == Visibility.Visible)
            {
                PlanPanel.BringIntoView();
            }
        }, DispatcherPriority.Loaded);
    }

    private void PlanChoice_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: OptimizationChoice choice } checkBox)
        {
            choice.IsSelected = checkBox.IsChecked == true;
            ApplyPlanButton.IsEnabled = !busy && choices.Any(item => item.IsSelected);
        }
    }

    private async void ApplyPlanButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = choices.Where(choice => choice.IsSelected).Select(choice => choice.Action).ToArray();
        if (busy || selected.Length == 0)
        {
            return;
        }

        string confirmation = string.Join(Environment.NewLine, selected.Select(action => $"• {action.Title} · PID {action.Process.Id}"));
        if (selected.Any(action => action.Kind == OptimizationActionKind.RequestClose))
        {
            confirmation += "\n\nSalve seu trabalho antes de continuar. Um pedido de fechamento pode encerrar janelas e abas abertas.";
        }

        if (MessageBox.Show(this, confirmation + "\n\nAplicar as ações selecionadas?", "Confirmar otimização",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        var resource = currentResource;
        ClearPlanActions();
        OptimizationResultPanel.Visibility = Visibility.Collapsed;
        await RunOperationAsync("Conferindo e aplicando as ações confirmadas…", "A operação foi interrompida. Verifique as prioridades da sessão.", async () =>
        {
            var (sample, result) = await Task.Run(() => ApplyCurrentActions(resource, selected));
            ShowOptimizationResult(result);
            var status = $"Ações aceitas: {result.Applied}. Ignoradas: {result.Skipped}.";
            if (result.Applied > 0)
            {
                try
                {
                    sample = await Task.Run(OptimizationSampler.Collect);
                }
                catch (Exception exception)
                {
                    Debug.WriteLine(exception);
                    OptimizationCards.ItemsSource = null;
                    SampleTimeText.Text = "";
                    SetStatus(status + " Faça uma nova verificação para atualizar as medições.");
                    return;
                }
            }

            ShowPerformanceSample(sample);
            SetStatus(status);
        });
    }

    private (OptimizationSnapshot Sample, OptimizationResult Result) ApplyCurrentActions(
        OptimizationResource resource, IReadOnlyList<OptimizationAction> selected)
    {
        var sample = OptimizationSampler.Collect();
        var current = OptimizationPlanner.Create(sample).First(card => card.Resource == resource);
        var stillApplicable = current.Actions.ToDictionary(action => (action.Kind, action.Process.Id, action.Process.StartTimeUtcTicks));
        var applicable = new List<OptimizationAction>();
        var skipped = new List<string>();

        foreach (var action in selected)
        {
            if (stillApplicable.TryGetValue((action.Kind, action.Process.Id, action.Process.StartTimeUtcTicks), out var confirmed))
            {
                applicable.Add(confirmed);
            }
            else
            {
                skipped.Add($"{action.Process.AppName} (PID {action.Process.Id}): a nova medição não confirmou a ação.");
            }
        }

        var result = applicable.Count == 0 ? new OptimizationResult(0, 0, []) : optimizer.Apply(resource, applicable);
        return (sample, new OptimizationResult(result.Applied, result.Skipped + skipped.Count, [.. result.Messages, .. skipped]));
    }

    private async void RestorePriorityButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync("Restaurando prioridades…", "Não foi possível restaurar todas as prioridades.", async () =>
        {
            var result = await Task.Run(optimizer.Restore);
            ShowOptimizationResult(result);
            SetStatus($"Restauração concluída. Prioridades restauradas: {result.Applied}. Ignoradas: {result.Skipped}.");
        });
    }

    private void ClearPlanActions()
    {
        choices = [];
        PlanActions.ItemsSource = null;
        PlanPanel.Visibility = Visibility.Collapsed;
        ApplyPlanButton.Visibility = Visibility.Collapsed;
        ApplyPlanButton.IsEnabled = false;
    }

    private void ShowOptimizationResult(OptimizationResult result)
    {
        OptimizationResultText.Text = string.Join(Environment.NewLine, result.Messages);
        OptimizationResultPanel.Visibility = result.Messages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowPerformanceSample(OptimizationSnapshot sample)
    {
        OptimizationCards.ItemsSource = OptimizationPlanner.Create(sample);
        SampleTimeText.Text = $"Amostra de {sample.CollectedAt:HH:mm:ss}";
    }

    private sealed class OptimizationChoice(OptimizationAction action)
    {
        public OptimizationAction Action { get; } = action;
        public bool IsSelected { get; set; }
    }
}
