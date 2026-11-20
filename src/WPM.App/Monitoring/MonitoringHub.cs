using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace WPM.App.Monitoring;

internal sealed class MonitoringHub
{
    private readonly SensorSampler sampler = new();
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private readonly HashSet<MonitoringPage> previews = [];
    private Task<SensorSample>? scan;
    private FrameTelemetry? frames;
    private HudWindow? hud;
    private HudOptions? hudOptions;

    internal static MonitoringHub Shared { get; } = new();

    private MonitoringHub()
    {
        timer.Tick += (_, _) => RequestScan();
        Application.Current.Exit += (_, _) => ShutDown();
        if (Application.Current.MainWindow is { } mainWindow)
        {
            mainWindow.Closed += (_, _) => hud?.Close();
        }
    }

    internal event EventHandler? Updated;

    internal SensorSample? Sample { get; private set; }
    internal FrameMeasurement? Frames { get; private set; }
    internal MonitoringSession? Session { get; private set; }
    internal bool HudActive => hud is not null;
    internal string FrameStatus => frames?.Status ?? "";

    internal void SetPreview(MonitoringPage view, bool active)
    {
        if (active)
        {
            previews.Add(view);
        }
        else
        {
            previews.Remove(view);
        }

        ApplyDemand();
    }

    internal void StartSession(int minutes, bool captureFrames)
    {
        if (Session?.IsRunning == true)
        {
            return;
        }

        Session = new MonitoringSession(minutes, captureFrames);
        ApplyDemand();
        Updated?.Invoke(this, EventArgs.Empty);
    }

    internal void EndSession()
    {
        Session?.End(complete: false);
        ApplyDemand();
        Updated?.Invoke(this, EventArgs.Empty);
    }

    internal void UpdateHud(HudOptions options)
    {
        hudOptions = options;
        hud?.Render(options, Sample, Frames);
        ApplyDemand();
    }

    internal void ShowHud(HudOptions options)
    {
        hudOptions = options;
        hud = new HudWindow();
        hud.Closed += (_, _) =>
        {
            hud = null;
            ApplyDemand();
            Updated?.Invoke(this, EventArgs.Empty);
        };
        hud.Render(options, Sample, Frames);
        hud.Show();
        ApplyDemand();
        Updated?.Invoke(this, EventArgs.Empty);
    }

    internal void HideHud()
    {
        hud?.Close();
    }

    private void ApplyDemand()
    {
        if (hud is not null || Session?.IsRunning == true || previews.Count > 0)
        {
            if (!timer.IsEnabled)
            {
                timer.Start();
                RequestScan();
            }
        }
        else
        {
            timer.Stop();
        }

        bool trackFrames = (hud is not null && hudOptions?.NeedsFrames == true) ||
            Session is { IsRunning: true, CaptureFrames: true };
        if (trackFrames)
        {
            frames ??= new FrameTelemetry();
        }
        else if (frames is not null)
        {
            frames.Dispose();
            frames = null;
            Frames = null;
        }
    }

    private async void RequestScan()
    {
        if (scan is not null)
        {
            return;
        }

        scan = Task.Run(sampler.Read);
        try
        {
            Sample = await scan;
            Frames = frames?.ReadForeground();
            if (Session is { IsRunning: true } running)
            {
                running.Record(Sample, running.CaptureFrames ? Frames : null);
                if (running.Elapsed >= running.Duration)
                {
                    running.End(complete: true);
                    ApplyDemand();
                }
            }

            if (hud is not null && hudOptions is not null)
            {
                hud.Render(hudOptions, Sample, Frames);
            }
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
        }
        finally
        {
            scan = null;
        }

        Updated?.Invoke(this, EventArgs.Empty);
    }

    private void ShutDown()
    {
        timer.Stop();
        frames?.Dispose();
        if (scan is null)
        {
            sampler.Dispose();
        }
        else
        {
            _ = scan.ContinueWith(_ => sampler.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
