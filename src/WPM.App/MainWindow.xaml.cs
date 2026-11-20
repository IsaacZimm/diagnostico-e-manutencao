using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;

namespace WPM.App;

public partial class MainWindow : Window
{
    private bool busy;

    public MainWindow()
    {
        InitializeComponent();
        InitializeRepair();
    }

    private void OverviewTemporaryFiles_Click(object sender, RoutedEventArgs e) => SelectPage(TemporaryFilesPage);

    private void OverviewRepair_Click(object sender, RoutedEventArgs e) => SelectPage(WindowsRepairPage);

    private async Task RunOperationAsync(string started, string failed, Func<Task> operation)
    {
        if (busy)
        {
            return;
        }

        busy = true;
        UpdateControls();
        SetStatus(started);

        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            SetStatus(failed);
        }
        finally
        {
            busy = false;
            UpdateControls();
        }
    }

    private void UpdateControls()
    {
        AnalyzeButton.IsEnabled = !busy;
        OverviewAnalyzeButton.IsEnabled = !busy;
        ExportButton.IsEnabled = !busy && report is not null;
        VerifySystemButton.IsEnabled = !busy;
        OptimizationCards.IsEnabled = !busy;
        PlanActions.IsEnabled = !busy;
        ApplyPlanButton.IsEnabled = !busy && choices.Any(choice => choice.IsSelected);
        RestorePriorityButton.Visibility = optimizer.PendingPriorityChanges > 0 ? Visibility.Visible : Visibility.Collapsed;
        RestorePriorityButton.IsEnabled = !busy;
        TempScanButton.IsEnabled = !busy;
        TempCleanButton.IsEnabled = !busy && tempPreview is { FileCount: > 0 };
        StartRepairButton.IsEnabled = !busy;
        RunTestButton.IsEnabled = !busy;
        TestsOptions.IsEnabled = !busy;
        ScanProgress.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        if (busy)
        {
            e.Cancel = true;
            return;
        }

        if (e.Cancel || optimizer.PendingPriorityChanges == 0)
        {
            return;
        }

        try
        {
            optimizer.Restore();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }

        if (optimizer.PendingPriorityChanges > 0)
        {
            e.Cancel = MessageBox.Show(this,
                "Algumas prioridades ainda estão alteradas. Se fechar agora, elas poderão permanecer assim até os aplicativos afetados encerrarem. Fechar mesmo assim?",
                "Restaurar prioridades", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes;
        }

        if (e.Cancel)
        {
            UpdateControls();
        }
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        UIElementAutomationPeer.FromElement(StatusText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
