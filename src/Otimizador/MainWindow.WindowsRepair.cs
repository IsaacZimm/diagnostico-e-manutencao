using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace ResetExplode;

public partial class MainWindow
{
    [Flags]
    private enum RepairSelection
    {
        None = 0,
        CheckHealth = 1,
        ScanHealth = 2,
        RestoreHealth = 4,
        Sfc = 8
    }

    private sealed record RepairStep(RepairSelection Choice, string Name, string Executable, string Arguments);

    private const RepairSelection RepairActions = RepairSelection.RestoreHealth | RepairSelection.Sfc;
    private static readonly RepairStep[] RepairSteps =
    [
        new(RepairSelection.CheckHealth, "CheckHealth", "dism.exe", "/Online /Cleanup-Image /CheckHealth"),
        new(RepairSelection.ScanHealth, "ScanHealth", "dism.exe", "/Online /Cleanup-Image /ScanHealth"),
        new(RepairSelection.RestoreHealth, "RestoreHealth", "dism.exe", "/Online /NoRestart /Cleanup-Image /RestoreHealth"),
        new(RepairSelection.Sfc, "SFC", "sfc.exe", "/scannow")
    ];
    private (CheckBox Box, RepairSelection Choice)[] _windowsRepairChoices = [];
    private bool _windowsRepairRunning;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetOEMCP();

    private static Encoding WindowsRepairOemEncoding =>
        CodePagesEncodingProvider.Instance.GetEncoding((int)GetOEMCP()) ?? Encoding.UTF8;

    private void InitWindowsRepair()
    {
        _windowsRepairChoices =
        [
            (WindowsRepairCheckHealth, RepairSelection.CheckHealth),
            (WindowsRepairScanHealth, RepairSelection.ScanHealth),
            (WindowsRepairRestoreHealth, RepairSelection.RestoreHealth),
            (WindowsRepairSfc, RepairSelection.Sfc)
        ];
        WindowsRepairCheckHealth.IsChecked = true;
        UpdateWindowsRepairSelection();
    }

    private RepairSelection GetWindowsRepairSelection()
    {
        var selection = RepairSelection.None;
        foreach (var (box, choice) in _windowsRepairChoices)
            if (box.IsChecked == true)
                selection |= choice;
        return selection;
    }

    private void WindowsRepairPreset_Click(object sender, RoutedEventArgs e)
    {
        var selection = Enum.Parse<RepairSelection>((string)((RadioButton)sender).Tag);
        foreach (var (box, choice) in _windowsRepairChoices)
            box.IsChecked = (selection & choice) != 0;
        UpdateWindowsRepairSelection();
    }

    private void WindowsRepairSelection_Click(object sender, RoutedEventArgs e) => UpdateWindowsRepairSelection();

    private void UpdateWindowsRepairSelection()
    {
        var selection = GetWindowsRepairSelection();
        foreach (RadioButton preset in WindowsRepairPresets.Children)
            preset.IsChecked = Enum.Parse<RepairSelection>((string)preset.Tag) == selection;

        WindowsRepairPlan.ItemsSource = RepairSteps.Where(step => (selection & step.Choice) != 0)
            .Select((step, index) => $"{index + 1}. {step.Executable} {step.Arguments}")
            .ToArray();
        BtnWindowsRepair.IsEnabled = selection != RepairSelection.None;
        WindowsRepairEmptyPlan.Visibility = selection == RepairSelection.None ? Visibility.Visible : Visibility.Collapsed;
        WindowsRepairRestart.IsEnabled = (selection & RepairActions) != 0;
        if (!WindowsRepairRestart.IsEnabled) WindowsRepairRestart.IsChecked = false;
    }

    private async void BtnWindowsRepair_Click(object sender, RoutedEventArgs e)
    {
        if (_windowsRepairRunning) return;
        var selection = GetWindowsRepairSelection();
        if (selection == RepairSelection.None || !RequireAdmin()) return;

        bool restartRequested = (selection & RepairActions) != 0 && WindowsRepairRestart.IsChecked == true;
        bool sequenceCompleted = false;
        _windowsRepairRunning = true;
        WindowsRepairOptions.IsEnabled = false;
        WindowsRepairOutput.Clear();
        WindowsRepairOutput.Visibility = Visibility.Visible;
        WindowsRepairProgress.Visibility = Visibility.Visible;
        WindowsRepairProgress.IsIndeterminate = true;
        WindowsRepairStatus.Visibility = Visibility.Visible;

        try
        {
            sequenceCompleted = await RunWindowsRepairAsync(selection, RunWindowsRepairCommandAsync);
            if (sequenceCompleted && restartRequested)
                await RestartAfterWindowsRepairAsync();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            WindowsRepairStatus.Text = sequenceCompleted
                ? "Sequência concluída. Falha ao solicitar a reinicialização. Confira a saída."
                : "A sequência foi interrompida. Confira os detalhes na saída.";
            AppendWindowsRepairOutput(ex.Message);
        }
        finally
        {
            WindowsRepairProgress.IsIndeterminate = false;
            WindowsRepairProgress.Visibility = Visibility.Collapsed;
            WindowsRepairRestart.IsChecked = false;
            WindowsRepairOptions.IsEnabled = true;
            _windowsRepairRunning = false;
        }
    }

    private async Task<bool> RunWindowsRepairAsync(
        RepairSelection selection, Func<string, string, Encoding, Task<int>> runCommand)
    {
        var steps = RepairSteps.Where(step => (selection & step.Choice) != 0).ToArray();
        var oemEncoding = WindowsRepairOemEncoding;
        for (int index = 0; index < steps.Length; index++)
        {
            var step = steps[index];
            WindowsRepairStatus.Text = $"{index + 1} de {steps.Length} · Executando {step.Name}…";
            var encoding = step.Choice == RepairSelection.Sfc ? Encoding.Unicode : oemEncoding;
            int exitCode = await runCommand(step.Executable, step.Arguments, encoding);

            if (exitCode == 3010 && step.Choice != RepairSelection.Sfc)
            {
                bool finalStep = index == steps.Length - 1;
                WindowsRepairStatus.Text = finalStep
                    ? "O DISM concluiu e pediu uma reinicialização do Windows."
                    : "O DISM pediu uma reinicialização. Reinicie o Windows e depois execute os comandos que ficaram pendentes.";
                return finalStep;
            }

            if (exitCode != 0)
            {
                WindowsRepairStatus.Text = $"{step.Name} retornou o código {exitCode}. Confira os detalhes na saída.";
                return false;
            }
        }

        WindowsRepairStatus.Text = "Sequência concluída. Confira o resultado dos comandos na saída.";
        return true;
    }

    private async Task RestartAfterWindowsRepairAsync()
    {
        WindowsRepairProgress.IsIndeterminate = false;
        WindowsRepairProgress.Visibility = Visibility.Collapsed;
        var prompt = new RepairRestartWindow { Owner = this };
        if (prompt.ShowDialog() != true)
        {
            WindowsRepairStatus.Text += " Reinicialização cancelada.";
            return;
        }

        // A espera acontece no Otimizador. Um /t maior que zero faria o Windows forçar o fechamento dos aplicativos.
        WindowsRepairStatus.Text = "Solicitando a reinicialização do Windows…";
        int exitCode = await RunWindowsRepairCommandAsync("shutdown.exe", "/r /t 0", WindowsRepairOemEncoding);
        WindowsRepairStatus.Text = exitCode == 0
            ? "Reinicialização solicitada ao Windows."
            : $"O Windows retornou o código {exitCode} ao solicitar a reinicialização. Confira a saída.";
    }

    private async Task<int> RunWindowsRepairCommandAsync(string executable, string arguments, Encoding encoding)
    {
        AppendWindowsRepairOutput($"> {executable} {arguments}");
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, executable), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Falha ao iniciar {executable}.");
        await Task.WhenAll(
            ReadWindowsRepairOutputAsync(process.StandardOutput),
            ReadWindowsRepairOutputAsync(process.StandardError),
            process.WaitForExitAsync());
        AppendWindowsRepairOutput($"Código de saída: {process.ExitCode}");
        return process.ExitCode;
    }

    private async Task ReadWindowsRepairOutputAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
            if (!string.IsNullOrWhiteSpace(line))
                AppendWindowsRepairOutput(line.Trim());
    }

    private void AppendWindowsRepairOutput(string line)
    {
        WindowsRepairOutput.AppendText(line + Environment.NewLine);
        WindowsRepairOutput.ScrollToEnd();
    }

    private void WindowsRepair_Closing(object? sender, CancelEventArgs e)
    {
        if (!_windowsRepairRunning) return;

        e.Cancel = true;
        MessageBox.Show(this, "Aguarde o reparo do Windows terminar antes de fechar o programa.",
                        "Reparo do Windows", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
