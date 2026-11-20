using System.Windows;
using WPM.App.Maintenance;

namespace WPM.App;

public partial class MainWindow
{
    private readonly TempCleanup tempCleanup = new();
    private TempCleanupPreview? tempPreview;

    private async void TempScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy)
        {
            return;
        }

        tempPreview = null;
        TempResultsPanel.Visibility = Visibility.Collapsed;
        await RunOperationAsync("Verificando os temporários do seu perfil…", "Não foi possível verificar a pasta de temporários.", async () =>
        {
            var preview = await Task.Run(tempCleanup.Scan);
            tempPreview = preview;
            TempResultsHeading.Text = "Prévia da limpeza";
            TempResultsText.Text = $"{FormatCount(preview.FileCount, "arquivo elegível", "arquivos elegíveis")} · {FormatBytes(preview.Bytes)} em arquivos."
                + $"\n{FormatCount(preview.Skipped, "item ignorado", "itens ignorados")} por critérios de elegibilidade ou de acesso."
                + (preview.Issue is null ? "" : $"\n{preview.Issue}");
            TempResultsPanel.Visibility = Visibility.Visible;
            SetStatus(preview.FileCount > 0 ? "Confira a prévia antes de limpar." : "Nenhum arquivo elegível foi encontrado.");
        });
    }

    private async void TempCleanButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy || tempPreview is not { FileCount: > 0 } preview)
        {
            return;
        }

        string confirmation = $"Excluir permanentemente {FormatCount(preview.FileCount, "arquivo da prévia", "arquivos da prévia")} ({FormatBytes(preview.Bytes)})?\n\n"
            + "Arquivos em uso, movidos ou alterados desde a verificação serão ignorados.";
        if (MessageBox.Show(this, confirmation, "Confirmar limpeza de temporários", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        tempPreview = null;
        TempResultsPanel.Visibility = Visibility.Collapsed;
        await RunOperationAsync("Limpando os temporários confirmados…", "A limpeza foi interrompida. Faça uma nova verificação.", async () =>
        {
            var result = await Task.Run(() => tempCleanup.Clean(preview));
            TempResultsHeading.Text = "Resultado da limpeza";
            TempResultsText.Text = $"{FormatCount(result.Deleted, "arquivo removido", "arquivos removidos")} · {FormatBytes(result.Bytes)}."
                + $"\n{FormatCount(result.Skipped, "arquivo ignorado", "arquivos ignorados")} da prévia."
                + (result.Issue is null ? "" : $"\n{result.Issue}");
            TempResultsPanel.Visibility = Visibility.Visible;
            SetStatus(result.Issue is null ? "Limpeza concluída." : "Confira o resultado da limpeza.");
        });
    }

    private static string FormatCount(int count, string singular, string plural)
    {
        return $"{count:N0} {(count == 1 ? singular : plural)}";
    }

    private static string FormatBytes(long bytes)
    {
        const double MiB = 1024d * 1024;
        const double GiB = MiB * 1024;

        return bytes switch
        {
            >= (long)GiB => $"{bytes / GiB:N2} GiB",
            >= (long)MiB => $"{bytes / MiB:N2} MiB",
            >= 1024 => $"{bytes / 1024d:N1} KiB",
            _ => $"{bytes:N0} bytes"
        };
    }
}
