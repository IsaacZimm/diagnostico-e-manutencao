using System.Windows;
using System.Windows.Controls;
using WPM.App.Diagnostics;

namespace WPM.App;

public partial class MainWindow
{
    private CancellationTokenSource? testCancellation;

    private void OverviewTests_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, NetworkTestsLink) && !busy)
        {
            NetworkTestOption.IsChecked = true;
        }

        SelectPage(TestsPage);
    }

    private async void RunTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy || TestsOptions.Children.OfType<RadioButton>().FirstOrDefault(button => button.IsChecked == true)
            is not { Tag: SystemTestKind kind, Content: string title })
        {
            return;
        }

        await RunOperationAsync($"Executando teste de {title.ToLowerInvariant()}…", "Não foi possível concluir o teste.", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            testCancellation = cancellation;
            TestOutcomePanel.Visibility = Visibility.Collapsed;
            CancelTestButton.Visibility = Visibility.Visible;
            CancelTestButton.IsEnabled = true;

            try
            {
                var result = await SystemTests.RunAsync(kind, cancellation.Token);
                var status = result.Status switch
                {
                    SystemTestStatus.Passed => "Concluído",
                    SystemTestStatus.Unavailable => "Indisponível",
                    _ => "Falhou"
                };

                ShowTestOutcome(title, status, result.Summary, result.Items);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                ShowTestOutcome(title, "Cancelado", "O teste foi interrompido.", []);
            }
            finally
            {
                testCancellation = null;
                CancelTestButton.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void CancelTestButton_Click(object sender, RoutedEventArgs e)
    {
        CancelTestButton.IsEnabled = false;
        testCancellation?.Cancel();
    }

    private void ShowTestOutcome(string title, string status, string summary, IReadOnlyList<DiagnosticItem> items)
    {
        TestOutcomeHeading.Text = $"{title} · {status}";
        TestOutcomeSummary.Text = summary;
        TestOutcomeItems.ItemsSource = items;
        TestOutcomePanel.Visibility = Visibility.Visible;
        SetStatus(TestOutcomeHeading.Text);
    }
}
