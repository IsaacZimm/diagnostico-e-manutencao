using System.Globalization;

namespace WPM.App.Optimization;

internal sealed record DiskReading(string Name, double Utilization, double? BytesPerSecond = null);

internal sealed class DiskTelemetry : IDisposable
{
    private readonly PdhCounter idleCounter;
    private readonly PdhCounter? throughputCounter;

    private DiskTelemetry(PdhCounter idleCounter, PdhCounter? throughputCounter)
    {
        this.idleCounter = idleCounter;
        this.throughputCounter = throughputCounter;
    }

    public static DiskTelemetry? Open()
    {
        var idle = PdhCounter.TryOpen(@"\PhysicalDisk(*)\% Idle Time");
        return idle is null ? null : new DiskTelemetry(idle, PdhCounter.TryOpen(@"\PhysicalDisk(*)\Disk Bytes/sec"));
    }

    public DiskReading? Read()
    {
        var idle = idleCounter.Collect();
        var throughput = throughputCounter?.Collect() ?? [];
        DiskReading? busiest = null;
        foreach (var reading in idle)
        {
            string[] parts = reading.Instance.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int diskNumber))
            {
                continue; // Ignore the synthetic _Total counter.
            }

            var rate = throughput.FirstOrDefault(item => item.Instance == reading.Instance && item.Value >= 0);
            double? bytesPerSecond = rate.Instance is null ? null : rate.Value;
            var utilization = 100 - Math.Clamp(reading.Value, 0, 100);
            if (busiest is null || utilization > busiest.Utilization
                || utilization == busiest.Utilization && bytesPerSecond.GetValueOrDefault() > busiest.BytesPerSecond.GetValueOrDefault())
            {
                string name = parts.Length > 1 ? $"Disco {diskNumber} · {parts[1]}" : $"Disco {diskNumber}";
                busiest = new DiskReading(name, utilization, bytesPerSecond);
            }
        }

        return busiest;
    }

    public void Dispose()
    {
        idleCounter.Dispose();
        throughputCounter?.Dispose();
    }
}
