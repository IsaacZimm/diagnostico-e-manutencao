using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace WPM.App.Monitoring;

internal sealed record FrameMeasurement(int Pid, string Process, double Fps, double AverageMs, double SlowFrameMs);

internal sealed class FrameTelemetry : IDisposable
{
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3d9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private readonly object sync = new();
    private readonly Dictionary<(Guid Provider, ulong Pointer), Queue<double>> swapchains = [];
    private TraceEventSession? activeSession;
    private bool disposed;
    private int trackedPid;
    private long lastPresentation;
    private string status = "Iniciando captura de quadros do Windows…";

    public string Status => Volatile.Read(ref status);

    public FrameTelemetry()
    {
        new Thread(Capture) { IsBackground = true, Name = "Apresentação DXGI e D3D9" }.Start();
    }

    public FrameMeasurement? ReadForeground()
    {
        int pid = ForegroundProcess.Id;
        double[] timestamps;
        lock (sync)
        {
            if (pid != trackedPid)
            {
                trackedPid = pid;
                swapchains.Clear();
                lastPresentation = 0;
            }

            if (pid <= 0 || swapchains.Count == 0 || lastPresentation == 0 ||
                Stopwatch.GetElapsedTime(lastPresentation) > TimeSpan.FromSeconds(2))
            {
                return null;
            }

            // One process can present through multiple swap chains. Report the busiest
            // chain's recent window instead of adding their FPS together.
            double cutoff = swapchains.Values.Max(queue => queue.Last()) - 1_400;
            var dominant = swapchains.Values.Where(queue => queue.Count >= 3)
                .MaxBy(queue => queue.Count(time => time >= cutoff));
            if (dominant is null)
            {
                return null;
            }

            timestamps = dominant.Where(time => time >= cutoff).ToArray();
        }

        var intervals = timestamps.Zip(timestamps.Skip(1), (start, finish) => finish - start)
            .Where(interval => interval is > 0 and <= 2_000)
            .ToArray();
        if (intervals.Length < 2)
        {
            return null;
        }

        Array.Sort(intervals);
        double mean = intervals.Average();
        double slow = intervals[(int)Math.Ceiling(intervals.Length * 0.99) - 1];
        return new(pid, ForegroundProcess.Name(pid), 1000 / mean, mean, slow);
    }

    private void Capture()
    {
        try
        {
            string sessionName = $"DiagnosticoManutencao-Frames-{Guid.NewGuid():N}";
            using var session = new TraceEventSession(sessionName,
                TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate |
                TraceEventSessionOptions.NoPerProcessorBuffering)
            {
                BufferSizeMB = 16,
                StopOnDispose = true
            };
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }
                activeSession = session;
            }

            session.EnableProvider(DxgiProvider, TraceEventLevel.Informational);
            session.EnableProvider(D3d9Provider, TraceEventLevel.Informational);
            var source = session.Source;
            source.AllEvents += RecordPresentation;
            Volatile.Write(ref status, "Captura ativa. Coloque um aplicativo DXGI ou Direct3D 9 em primeiro plano.");
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }
            }
            source.Process();
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            Volatile.Write(ref status, "O Windows não disponibilizou a captura de FPS. Pode ser necessário executar como administrador.");
        }
        finally
        {
            lock (sync)
            {
                activeSession = null;
            }
        }
    }

    private void RecordPresentation(TraceEvent data)
    {
        bool dxgi = data.ProviderGuid == DxgiProvider && (int)data.ID is (42 or 55);
        if ((!dxgi && (data.ProviderGuid != D3d9Provider || (int)data.ID != 1)) ||
            data.ProcessID != Volatile.Read(ref trackedPid))
        {
            return;
        }

        byte[] payload = data.EventData();
        int pointerSize = data.PointerSize;
        if (payload.Length < pointerSize + sizeof(uint) || pointerSize is not (4 or 8))
        {
            return;
        }

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(pointerSize));
        if (dxgi && (flags & 1) != 0)
        {
            return; // DXGI_PRESENT_TEST only checks readiness.
        }
        ulong pointer = pointerSize == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(payload) : BinaryPrimitives.ReadUInt32LittleEndian(payload);
        double now = data.TimeStampRelativeMSec;
        lock (sync)
        {
            if (disposed || data.ProcessID != trackedPid)
            {
                return;
            }
            var key = (data.ProviderGuid, pointer);
            if (!swapchains.TryGetValue(key, out Queue<double>? timestamps))
            {
                if (swapchains.Count >= 64)
                {
                    var expired = swapchains.MinBy(pair => pair.Value.Last()).Key;
                    swapchains.Remove(expired);
                }
                timestamps = new Queue<double>();
                swapchains.Add(key, timestamps);
            }

            timestamps.Enqueue(now);
            while (timestamps.Count > 2048 || timestamps.Peek() < now - 1_400)
            {
                timestamps.Dequeue();
            }

            lastPresentation = Stopwatch.GetTimestamp();
        }
    }

    public void Dispose()
    {
        TraceEventSession? session;
        lock (sync)
        {
            disposed = true;
            session = activeSession;
            swapchains.Clear();
        }
        if (session is not null)
        {
            try
            {
                session.Stop();
            }
            catch (Exception error)
            {
                Debug.WriteLine(error);
            }
        }
    }
}

internal static class ForegroundProcess
{
    public static int Id
    {
        get
        {
            nint window = GetForegroundWindow();
            return window != 0 && GetWindowThreadProcessId(window, out int pid) != 0 && pid != Environment.ProcessId
                ? pid : 0;
        }
    }

    public static string Name(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return $"PID {pid}";
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern nint GetForegroundWindow();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern uint GetWindowThreadProcessId(nint window, out int pid);
}
