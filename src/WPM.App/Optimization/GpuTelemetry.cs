using System.Globalization;
using System.Text.RegularExpressions;

namespace WPM.App.Optimization;

internal sealed record GpuReading(double Utilization, IReadOnlyDictionary<int, double> Processes)
{
    public IReadOnlyDictionary<int, double> BusiestEngineProcesses { get; init; } = new Dictionary<int, double>();
}

internal sealed class GpuTelemetry : IDisposable
{
    private const string EngineCounter = @"\GPU Engine(*)\Utilization Percentage";
    private static readonly Regex EngineInstance = new(
        @"\Apid_([0-9]+)_luid_0x([0-9a-f]{1,8})_0x([0-9a-f]{1,8})_phys_([0-9]+)_eng_([0-9]+)_engtype_.+\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    private readonly PdhCounter counter;

    private GpuTelemetry(PdhCounter counter)
    {
        this.counter = counter;
    }

    public static GpuTelemetry? Open()
    {
        var counter = PdhCounter.TryOpen(EngineCounter);
        return counter is null ? null : new GpuTelemetry(counter);
    }

    public GpuReading? Read()
    {
        return Summarize(counter.Collect());
    }

    internal static GpuReading? Summarize(IEnumerable<PdhReading> readings)
    {
        var contributions = new Dictionary<(int ProcessId, EngineId Engine), double>();

        foreach (var reading in readings)
        {
            if (!double.IsFinite(reading.Value) || reading.Value < 0
                || !TryParseInstance(reading.Instance, out var processId, out var engine))
            {
                continue;
            }

            var key = (processId, engine);
            contributions[key] = Math.Max(contributions.GetValueOrDefault(key), Math.Min(reading.Value, 100));
        }

        if (contributions.Count == 0)
        {
            return null;
        }

        var engineTotals = new Dictionary<EngineId, double>();
        var processPeaks = new Dictionary<int, double>();
        foreach (var (key, percent) in contributions)
        {
            engineTotals[key.Engine] = engineTotals.GetValueOrDefault(key.Engine) + percent;
            processPeaks[key.ProcessId] = Math.Max(processPeaks.GetValueOrDefault(key.ProcessId), percent);
        }

        // Engines can execute in parallel. Only combine time on the same engine before picking the peak.
        var busiest = engineTotals.MaxBy(engine => engine.Value);
        var onBusiest = contributions.Where(contribution => contribution.Key.Engine == busiest.Key)
            .ToDictionary(contribution => contribution.Key.ProcessId, contribution => contribution.Value);
        return new GpuReading(Math.Min(busiest.Value, 100), processPeaks) { BusiestEngineProcesses = onBusiest };
    }

    private static bool TryParseInstance(string name, out int processId, out EngineId engine)
    {
        processId = 0;
        engine = default;
        var match = EngineInstance.Match(name);
        if (!match.Success || !TryParseDecimal(match.Groups[1].ValueSpan, out processId)
            || !uint.TryParse(match.Groups[2].ValueSpan, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var high)
            || !uint.TryParse(match.Groups[3].ValueSpan, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var low)
            || !TryParseDecimal(match.Groups[4].ValueSpan, out var adapter)
            || !TryParseDecimal(match.Groups[5].ValueSpan, out var index))
        {
            return false;
        }

        engine = new EngineId(high, low, adapter, index);
        return true;
    }

    private static bool TryParseDecimal(ReadOnlySpan<char> value, out int result)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }

    public void Dispose()
    {
        counter.Dispose();
    }

    private readonly record struct EngineId(uint LuidHigh, uint LuidLow, int PhysicalAdapter, int Index);
}
