using System.Windows;
using WPM.App.Repair;

namespace WPM.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0] == RepairBroker.WorkerArgument)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await RepairBroker.RunWorkerAsync(e.Args));
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
