using System.Diagnostics;
using System.Windows;
using WPM.App.Repair;

namespace WPM.App;

public partial class MainWindow
{
    private void InitializeRepair()
    {
        RepairOperationList.ItemsSource = RepairCommands.All.Select(command => new RepairOperationView(command)).ToArray();
        RepairOperationList.SelectedIndex = 0;
    }

    private async void StartRepairButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy || RepairOperationList.SelectedItem is not RepairOperationView entry)
        {
            return;
        }

        var command = entry.Command;
        if (command.ModifiesWindows && MessageBox.Show(this,
                $"{command.Description}\n\nExecutar esta operação na instalação atual do Windows?",
                command.Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunOperationAsync($"Preparando {command.Title}…", "Não foi possível acompanhar a operação do Windows.", async () =>
        {
            entry.Start();
            var progress = new Progress<RepairUpdate>(update => ShowRepairUpdate(entry, update));
            RepairResult result;
            try
            {
                result = await RepairBroker.RunAsync(command.Operation, ((IProgress<RepairUpdate>)progress).Report);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(exception);
                result = new RepairResult(null, "Não foi possível acompanhar a execução. Consulte o registro do Windows antes de tentar novamente.");
            }

            entry.Finish(result);
            SetStatus($"{command.Title} · {entry.Status}");
        });
    }

    private void ShowRepairUpdate(RepairOperationView entry, RepairUpdate update)
    {
        bool follow = ReferenceEquals(RepairOperationList.SelectedItem, entry)
            && RepairOutput.ExtentHeight - RepairOutput.ViewportHeight - RepairOutput.VerticalOffset <= 2;
        entry.Apply(update);
        if (follow && update.Text is not null)
        {
            RepairOutput.ScrollToEnd();
        }
    }
}
