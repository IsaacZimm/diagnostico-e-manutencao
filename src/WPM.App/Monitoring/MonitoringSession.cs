using System.Diagnostics;
using System.Globalization;
using LibreHardwareMonitor.Hardware;

namespace WPM.App.Monitoring;

internal enum LoadSignal { Vram, Ram, Temperature, Graphics, Processor, NoSaturation, MissingTelemetry }

internal sealed record PerformanceRow(string Resource, string Reading);

internal sealed class MonitoringSession(int minutes, bool captureFrames)
{
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("pt-BR");
    private readonly Stopwatch timer = Stopwatch.StartNew();
    private readonly Dictionary<LoadSignal, int> signals = [];
    private readonly Dictionary<int, FrameAggregate> frameAverages = [];

    public TimeSpan Duration { get; } = TimeSpan.FromMinutes(minutes);
    public TimeSpan Elapsed => timer.Elapsed;
    public bool IsRunning => timer.IsRunning;
    public bool CaptureFrames { get; } = captureFrames;
    public int Count { get; private set; }
    public bool Completed { get; private set; }

    public void Record(SensorSample sample, FrameMeasurement? frame)
    {
        if (!IsRunning)
        {
            return;
        }

        Count++;
        var signal = Assess(sample);
        signals[signal] = signals.GetValueOrDefault(signal) + 1;
        if (frame is not null)
        {
            if (!frameAverages.TryGetValue(frame.Pid, out FrameAggregate? aggregate))
            {
                aggregate = new FrameAggregate(frame.Process);
                frameAverages.Add(frame.Pid, aggregate);
            }

            aggregate.Count++;
            aggregate.Sum += frame.Fps;
        }
    }

    public void End(bool complete)
    {
        timer.Stop();
        Completed = complete;
    }

    public IReadOnlyList<PerformanceRow> Results()
    {
        if (Count == 0)
        {
            return [];
        }

        var rows = signals.OrderByDescending(entry => entry.Value)
            .Select(entry => new PerformanceRow(Label(entry.Key),
                string.Create(DisplayCulture, $"{entry.Value * 100d / Count:0.#}%")))
            .ToList();
        if (frameAverages.Values.MaxBy(frames => frames.Count) is { } dominant)
        {
            rows.Add(new($"Média de FPS de apresentação · {dominant.Process}",
                string.Create(DisplayCulture, $"{dominant.Sum / dominant.Count:0.#} FPS")));
        }

        return rows;
    }

    internal static LoadSignal Assess(SensorSample sample)
    {
        if (sample.VramLoad >= 97)
        {
            return LoadSignal.Vram;
        }
        if (sample.RamLoad >= 94)
        {
            return LoadSignal.Ram;
        }
        if (sample.CpuTemperature >= 95 || sample.GpuTemperature >= 90)
        {
            return LoadSignal.Temperature;
        }
        if (sample.GpuLoad >= 95)
        {
            return LoadSignal.Graphics;
        }
        if (sample.GpuLoad is < 85 && (sample.CpuLoad >= 90 || sample.CpuMaxThread >= 95))
        {
            return LoadSignal.Processor;
        }

        return sample.CpuLoad.HasValue && sample.GpuLoad.HasValue ? LoadSignal.NoSaturation : LoadSignal.MissingTelemetry;
    }

    internal static string Label(LoadSignal signal) => signal switch
    {
        LoadSignal.Vram => "Uso alto de memória da GPU",
        LoadSignal.Ram => "Uso alto de memória RAM",
        LoadSignal.Temperature => "Temperatura elevada",
        LoadSignal.Graphics => "Uso alto da GPU",
        LoadSignal.Processor => "Indício de limite no processador",
        LoadSignal.NoSaturation => "Sem saturação identificada",
        _ => "Telemetria insuficiente"
    };

    internal static IReadOnlyList<PerformanceRow> CurrentReadings(SensorSample sample, FrameMeasurement? frames)
    {
        var readings = new List<PerformanceRow>
        {
            new("Carga da CPU", SensorDisplay.Value(SensorType.Load, sample.CpuLoad)),
            new("Núcleo ou thread mais ocupado", SensorDisplay.Value(SensorType.Load, sample.CpuMaxThread)),
            new("Carga da GPU", SensorDisplay.Value(SensorType.Load, sample.GpuLoad)),
            new("Uso de memória da GPU", SensorDisplay.Value(SensorType.Load, sample.VramLoad)),
            new("Uso de RAM", SensorDisplay.Value(SensorType.Load, sample.RamLoad)),
            new("Temperatura da CPU", SensorDisplay.Value(SensorType.Temperature, sample.CpuTemperature)),
            new("Temperatura da GPU", SensorDisplay.Value(SensorType.Temperature, sample.GpuTemperature))
        };
        if (frames is not null)
        {
            readings.Add(new($"FPS de apresentação · {frames.Process}",
                string.Create(DisplayCulture, $"{frames.Fps:0.#} FPS")));
        }

        return readings;
    }

    private sealed class FrameAggregate(string process)
    {
        public string Process { get; } = process;
        public int Count { get; set; }
        public double Sum { get; set; }
    }
}
