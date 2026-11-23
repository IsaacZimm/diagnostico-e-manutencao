using System.Windows;
using System.Windows.Threading;

namespace ResetExplode;

public partial class RepairRestartWindow : Window
{
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };

    public RepairRestartWindow()
    {
        InitializeComponent();
        int secondsLeft = 30;
        CountdownText.Text = $"Reiniciando em {secondsLeft} segundos";

        _countdown.Tick += (_, _) =>
        {
            secondsLeft--;
            if (secondsLeft == 0)
                DialogResult = true;
            else
                CountdownText.Text = secondsLeft == 1
                    ? "Reiniciando em 1 segundo"
                    : $"Reiniciando em {secondsLeft} segundos";
        };
        ContentRendered += (_, _) => _countdown.Start();
        Closed += (_, _) => _countdown.Stop();
    }
}
