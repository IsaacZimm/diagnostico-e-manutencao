using System.Diagnostics;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WPM.App.Diagnostics;

namespace WPM.App;

public partial class MainWindow
{
    private const double DiagnosticWideLayout = 700;
    private DiagnosticReport? report;
    private DiagnosticCategory diagnosticCategory = DiagnosticCategory.System;
    private TabItem? reportHost;

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e) => await AnalyzeAsync();

    private async void OverviewAnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        bool refresh = report is not null;
        MainNavigation.SelectedItem = DiagnosticPage;
        if (refresh)
        {
            await AnalyzeAsync();
        }
    }

    private void OverviewCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DiagnosticCategory category })
        {
            SelectPage(category == DiagnosticCategory.Network ? NetworkPage : DiagnosticPage);
        }
    }

    private void OverviewSensors_Click(object sender, RoutedEventArgs e) => SelectPage(SensorsPage);

    private void SelectPage(TabItem page)
    {
        MainNavigation.SelectedItem = page;
        page.Focus();
    }

    private async void MainNavigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainNavigation || DiagnosticSurface is null || MainNavigation.SelectedItem is not TabItem page)
        {
            return;
        }

        DiagnosticCategory? category = page.Name switch
        {
            nameof(DiagnosticPage) => DiagnosticCategory.System,
            nameof(NetworkPage) => DiagnosticCategory.Network,
            nameof(GraphicsPage) => DiagnosticCategory.Graphics,
            nameof(StoragePage) => DiagnosticCategory.Storage,
            nameof(PowerPage) => DiagnosticCategory.Power,
            nameof(DevicesPage) => DiagnosticCategory.Devices,
            _ => null
        };
        if (category is null)
        {
            return;
        }

        reportHost ??= DiagnosticPage;
        if (reportHost != page)
        {
            reportHost.Content = null;
            page.Content = DiagnosticSurface;
            reportHost = page;
        }

        diagnosticCategory = category.Value;
        DiagnosticHeading.Text = (string)page.Header;
        NetworkTestsLink.Visibility = category == DiagnosticCategory.Network ? Visibility.Visible : Visibility.Collapsed;
        if (report is null)
        {
            await AnalyzeAsync();
            return;
        }

        ShowDiagnosticCards();
        DiagnosticScroll.ScrollToTop();
    }

    private async Task AnalyzeAsync()
    {
        await RunOperationAsync("Consultando as informações do computador…", "Não foi possível concluir o diagnóstico.", async () =>
        {
            var currentReport = await Task.Run(DiagnosticCollector.Collect);
            report = currentReport;
            EmptyPanel.Visibility = Visibility.Collapsed;
            DiagnosticCards.Visibility = Visibility.Visible;
            ShowDiagnosticCards();
            DiagnosticScroll.ScrollToTop();

            var collectedAt = currentReport.CollectedAt.ToString("dd/MM/yyyy 'às' HH:mm");
            OverviewSnapshotText.Text = $"Última análise em {collectedAt}. Abra uma categoria ou atualize as informações.";
            OverviewAnalyzeButton.Content = "Atualizar análise";
            var hasUnavailableData = currentReport.Sections.Any(section => section.Issue is not null);
            SetStatus(hasUnavailableData
                ? $"Análise de {collectedAt} concluída. Algumas informações ficaram indisponíveis."
                : $"Análise de {collectedAt} concluída.");
        });
    }

    private void DiagnosticCards_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (report is not null && (e.PreviousSize.Width >= DiagnosticWideLayout) != (e.NewSize.Width >= DiagnosticWideLayout))
        {
            ShowDiagnosticCards();
        }
    }

    private void ShowDiagnosticCards()
    {
        if (report is null)
        {
            return;
        }

        var sections = report.Sections.Where(section => section.Category == diagnosticCategory).ToArray();
        bool twoColumns = sections.Length > 1 && diagnosticCategory != DiagnosticCategory.Network
            && DiagnosticCards.ActualWidth >= DiagnosticWideLayout;

        DiagnosticGapColumn.Width = new GridLength(twoColumns ? 12 : 0);
        DiagnosticRightColumn.Width = twoColumns ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        if (!twoColumns)
        {
            Results.ItemsSource = sections;
            DiagnosticSecondaryResults.ItemsSource = null;
            return;
        }

        var left = new List<DiagnosticSection>();
        var right = new List<DiagnosticSection>();
        int leftRows = 0;
        int rightRows = 0;
        foreach (var section in sections)
        {
            int rows = section.Items.Count + 2 + (section.Issue is null ? 0 : 1);
            if (leftRows <= rightRows)
            {
                left.Add(section);
                leftRows += rows;
            }
            else
            {
                right.Add(section);
                rightRows += rows;
            }
        }

        Results.ItemsSource = left;
        DiagnosticSecondaryResults.ItemsSource = right;
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy || report is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Exportar relatório",
            Filter = "Arquivo de texto (*.txt)|*.txt",
            DefaultExt = ".txt",
            FileName = $"diagnostico-{report.CollectedAt:yyyy-MM-dd_HHmmss}.txt"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, DiagnosticReportText.Format(report));
            SetStatus($"Relatório salvo como {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            Debug.WriteLine(exception);
            SetStatus("O relatório não pôde ser salvo. Tente escolher outra pasta.");
        }
    }
}
