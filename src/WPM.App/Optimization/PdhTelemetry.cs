using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WPM.App.Optimization;

internal readonly record struct PdhReading(string Instance, double Value);

internal sealed class PdhCounter : IDisposable
{
    private const uint Success = 0;
    private const uint MoreData = 0x800007D2;
    private const uint FormatDouble = 0x00000200;
    private const uint DoNotCapAt100 = 0x00008000;
    private const uint ValidData = 0;
    private const uint NewData = 1;

    private static readonly int ItemSize = Marshal.SizeOf<CounterItem>();

    private readonly QueryHandle query;
    private readonly nint counter;

    private PdhCounter(QueryHandle query, nint counter)
    {
        this.query = query;
        this.counter = counter;
    }

    public static PdhCounter? TryOpen(string path)
    {
        uint status = PdhOpenQuery(null, 0, out var query);
        if (status == Success && !query.IsInvalid
            && PdhAddEnglishCounter(query, path, 0, out nint counter) == Success)
        {
            return new PdhCounter(query, counter);
        }

        query.Dispose();
        return null;
    }

    public IReadOnlyList<PdhReading> Collect()
    {
        if (query.IsClosed || PdhCollectQueryData(query) != Success)
        {
            return [];
        }

        const uint format = FormatDouble | DoNotCapAt100;
        uint bytes = 0;
        uint status = PdhGetFormattedCounterArray(counter, format, ref bytes, out _, 0);
        // The wildcard instance set may grow between the sizing and reading calls.
        for (int attempt = 0; attempt < 3 && status == MoreData && bytes is > 0 and <= int.MaxValue; attempt++)
        {
            uint capacity = bytes;
            nint buffer = Marshal.AllocHGlobal((int)capacity);
            try
            {
                status = PdhGetFormattedCounterArray(counter, format, ref bytes, out uint count, buffer);
                if (status == Success && count <= capacity / (uint)ItemSize)
                {
                    return ReadItems(buffer, (int)count);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return [];
    }

    public void Dispose() => query.Dispose();

    private static IReadOnlyList<PdhReading> ReadItems(nint buffer, int count)
    {
        var readings = new List<PdhReading>(count);
        for (int index = 0; index < count; index++)
        {
            var item = Marshal.PtrToStructure<CounterItem>(buffer + index * ItemSize);
            if (item.Value.Status is not (ValidData or NewData) || !double.IsFinite(item.Value.Number))
            {
                continue;
            }

            if (Marshal.PtrToStringUni(item.Name) is { Length: > 0 } name)
            {
                readings.Add(new PdhReading(name, item.Value.Number));
            }
        }

        return readings;
    }

    private sealed class QueryHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => PdhCloseQuery(handle) == Success;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public nint Name;
        public CounterValue Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue
    {
        public uint Status;
        public double Number;
    }

    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQuery(string? source, nuint userData, out QueryHandle query);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounter(QueryHandle query, string path, nuint userData, out nint counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(QueryHandle query);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint count, nint buffer);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(nint query);
}
