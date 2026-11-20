using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32.SafeHandles;

namespace WPM.App.Optimization;

internal enum OptimizationAppKind { Other, Browser, Discretionary, SolidEdge, SolidWorks, Photoshop }

internal sealed record OptimizationProcess(
    int Id,
    long StartTimeUtcTicks,
    string ProcessName,
    string AppName,
    OptimizationAppKind Kind,
    double? CpuPercent,
    long? WorkingSetBytes,
    double? IoBytesPerSecond = null,
    bool CanAdjust = false,
    bool IsNormalPriority = false);

internal sealed record OptimizationSnapshot(
    double? CpuPercent,
    long? AvailableBytes,
    long? TotalBytes,
    IReadOnlyList<OptimizationProcess> Processes,
    DiskReading? Disk,
    GpuReading? Gpu,
    DateTimeOffset CollectedAt,
    int LogicalProcessorCount);

internal static class OptimizationSampler
{
    private const ushort AllProcessorGroups = 0xffff;
    private const uint QueryLimitedInformation = 0x1000;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);
    private static readonly Dictionary<string, KnownApp> KnownApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["edge"] = new("Solid Edge", OptimizationAppKind.SolidEdge),
        ["sldworks"] = new("SOLIDWORKS", OptimizationAppKind.SolidWorks),
        ["photoshop"] = new("Adobe Photoshop", OptimizationAppKind.Photoshop),
        ["msedge"] = new("Microsoft Edge", OptimizationAppKind.Browser),
        ["chrome"] = new("Google Chrome", OptimizationAppKind.Browser),
        ["firefox"] = new("Mozilla Firefox", OptimizationAppKind.Browser),
        ["brave"] = new("Brave", OptimizationAppKind.Browser),
        ["opera"] = new("Opera", OptimizationAppKind.Browser),
        ["vivaldi"] = new("Vivaldi", OptimizationAppKind.Browser),
        ["steam"] = new("Steam", OptimizationAppKind.Discretionary),
        ["EpicGamesLauncher"] = new("Epic Games Launcher", OptimizationAppKind.Discretionary),
        ["Spotify"] = new("Spotify", OptimizationAppKind.Discretionary),
        ["Battle.net"] = new("Battle.net", OptimizationAppKind.Discretionary),
        ["GalaxyClient"] = new("GOG Galaxy", OptimizationAppKind.Discretionary)
    };

    public static OptimizationSnapshot Collect()
    {
        int logicalProcessorCount = (int)GetActiveProcessorCount(AllProcessorGroups);
        bool hasOneCpuGroup = GetActiveProcessorGroupCount() == 1;
        int? sessionId = ProcessIdToSessionId(Environment.ProcessId, out int currentSessionId) ? currentSessionId : null;

        using var disk = DiskTelemetry.Open();
        using var gpu = GpuTelemetry.Open();
        _ = disk?.Read();
        _ = gpu?.Read();

        CpuTimes? beforeCpu = hasOneCpuGroup ? ReadCpuTimes() : null;
        var initialProcesses = ReadProcesses(sessionId: null).ToDictionary(process => process.Id);
        Thread.Sleep(SampleInterval);
        CpuTimes? afterCpu = hasOneCpuGroup ? ReadCpuTimes() : null;
        DiskReading? diskReading = disk?.Read();
        GpuReading? gpuReading = gpu?.Read();
        (long? available, long? total) = ReadMemory();

        List<ProcessReading> recentProcesses = ReadProcesses(sessionId, includeDetails: true);
        var processes = new List<OptimizationProcess>(recentProcesses.Count);
        foreach (ProcessReading current in recentProcesses)
        {
            initialProcesses.TryGetValue(current.Id, out ProcessReading? initial);
            if (current.StartTimeUtcTicks == 0 || initial?.StartTimeUtcTicks != current.StartTimeUtcTicks)
            {
                initial = null;
            }

            KnownApps.TryGetValue(current.ProcessName, out KnownApp? app);
            bool canAdjust = current.CanAdjust && initial is not null;
            processes.Add(new OptimizationProcess(current.Id, current.StartTimeUtcTicks, current.ProcessName,
                app?.Name ?? current.ProcessName, app?.Kind ?? OptimizationAppKind.Other,
                ProcessCpuPercent(initial, current, logicalProcessorCount), current.WorkingSetBytes,
                ProcessIoBytesPerSecond(initial, current), canAdjust, canAdjust && current.IsNormalPriority));
        }

        return new OptimizationSnapshot(SystemCpuPercent(beforeCpu, afterCpu), available, total, processes,
            diskReading, gpuReading, DateTimeOffset.Now, logicalProcessorCount);
    }

    private static List<ProcessReading> ReadProcesses(int? sessionId, bool includeDetails = false)
    {
        var readings = new List<ProcessReading>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception error) when (IsProcessError(error))
        {
            return readings;
        }

        foreach (Process process in processes)
        {
            using (process)
            {
                if (process.Id != 0)
                {
                    readings.Add(ReadProcess(process, sessionId, includeDetails));
                }
            }
        }

        return readings;
    }

    private static ProcessReading ReadProcess(Process process, int? sessionId, bool includeDetails)
    {
        string name = includeDetails ? ReadProperty(() => process.ProcessName) ?? "Processo" : "";
        using var handle = OpenProcess(QueryLimitedInformation, false, process.Id);
        long startTime = 0;
        ulong? cpuTicks = null;
        IoCounters? io = null;
        long? workingSet = null;
        long timestamp = Stopwatch.GetTimestamp();

        if (!handle.IsInvalid)
        {
            if (GetProcessTimes(handle, out long creation, out _, out ulong kernelTime, out ulong userTime) && creation > 0)
            {
                startTime = DateTime.FromFileTimeUtc(creation).Ticks;
                cpuTicks = kernelTime + userTime;
            }

            if (GetProcessIoCounters(handle, out IoCounters counters))
            {
                io = counters;
            }

            if (includeDetails && GetProcessMemoryInfo(handle, out var memory, (uint)Marshal.SizeOf<ProcessMemoryCounters>()))
            {
                workingSet = (long)memory.WorkingSetSize;
            }
        }

        if (includeDetails && workingSet is null)
        {
            workingSet = ReadProperty<long?>(() => process.WorkingSet64);
        }

        bool canAdjust = startTime > 0 && process.Id != Environment.ProcessId && sessionId.HasValue
            && KnownApps.TryGetValue(name, out KnownApp? app)
            && app.Kind is OptimizationAppKind.Browser or OptimizationAppKind.Discretionary
            && ReadProperty<int?>(() => process.SessionId) == sessionId
            && ReadProperty<nint?>(() => process.MainWindowHandle) is nint window && window != 0;
        bool isNormalPriority = canAdjust && GetPriorityClass(handle) == ProcessPriorityClass.Normal;

        return new ProcessReading(process.Id, name, startTime, cpuTicks, io, timestamp, workingSet, canAdjust, isNormalPriority);
    }

    private static T? ReadProperty<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception error) when (IsProcessError(error))
        {
            return default;
        }
    }

    private static bool IsProcessError(Exception error)
    {
        return error is Win32Exception or InvalidOperationException or UnauthorizedAccessException or SecurityException;
    }

    private static double? ProcessCpuPercent(ProcessReading? initial, ProcessReading recent, int logicalProcessors)
    {
        if (initial?.CpuTicks is not ulong initialTicks || recent.CpuTicks is not ulong recentTicks ||
            recentTicks < initialTicks || logicalProcessors <= 0)
        {
            return null;
        }

        long elapsed = Stopwatch.GetElapsedTime(initial.Timestamp, recent.Timestamp).Ticks;
        return elapsed > 0
            ? Math.Clamp(100d * (recentTicks - initialTicks) / elapsed / logicalProcessors, 0, 100)
            : null;
    }

    private static double? ProcessIoBytesPerSecond(ProcessReading? initial, ProcessReading recent)
    {
        if (initial?.Io is not IoCounters before || recent.Io is not IoCounters after ||
            after.ReadBytes < before.ReadBytes || after.WriteBytes < before.WriteBytes)
        {
            return null;
        }

        // Windows counts read/write I/O to files, networks, and devices together for each process.
        double bytes = (after.ReadBytes - before.ReadBytes) + (double)(after.WriteBytes - before.WriteBytes);
        double elapsed = Stopwatch.GetElapsedTime(initial.Timestamp, recent.Timestamp).TotalSeconds;
        return elapsed > 0 ? bytes / elapsed : null;
    }

    private static CpuTimes? ReadCpuTimes()
    {
        return GetSystemTimes(out ulong idle, out ulong kernel, out ulong user) ? new CpuTimes(idle, kernel, user) : null;
    }

    private static double? SystemCpuPercent(CpuTimes? first, CpuTimes? last)
    {
        if (first is not CpuTimes before || last is not CpuTimes after ||
            after.Idle < before.Idle || after.Kernel < before.Kernel || after.User < before.User)
        {
            return null;
        }

        // GetSystemTimes includes idle time in the kernel counter.
        double total = (after.Kernel - before.Kernel) + (double)(after.User - before.User);
        ulong idle = after.Idle - before.Idle;
        return total > 0 && idle <= total ? 100 * (total - idle) / total : null;
    }

    private static (long? Available, long? Total) ReadMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0 ||
            status.TotalPhysical > long.MaxValue || status.AvailablePhysical > status.TotalPhysical)
        {
            return (null, null);
        }

        return ((long)status.AvailablePhysical, (long)status.TotalPhysical);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);

    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(int processId, out int sessionId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32.dll")]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out ulong kernel, out ulong user);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);

    [DllImport("kernel32.dll")]
    private static extern ProcessPriorityClass GetPriorityClass(SafeProcessHandle process);

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(SafeProcessHandle process, out ProcessMemoryCounters counters, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadCount;
        public ulong WriteCount;
        public ulong OtherCount;
        public ulong ReadBytes;
        public ulong WriteBytes;
        public ulong OtherBytes;
    }

    private sealed record KnownApp(string Name, OptimizationAppKind Kind);

    private readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

    private sealed record ProcessReading(int Id, string ProcessName, long StartTimeUtcTicks, ulong? CpuTicks,
        IoCounters? Io, long Timestamp, long? WorkingSetBytes, bool CanAdjust, bool IsNormalPriority);
}
