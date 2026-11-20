using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using WPM.App.Optimization;

namespace WPM.App.Monitoring;

internal sealed record SensorReading(
    string Id, string DeviceId, string Device, string Name, HardwareType Component, SensorType Type,
    double? Value, double? Minimum = null, double? Maximum = null);

internal sealed record SensorSample(IReadOnlyList<SensorReading> Readings, bool LibraryAvailable)
{
    public double? CpuLoad => Find(HardwareType.Cpu, SensorType.Load, "CPU Total");
    public double? CpuMaxThread => Readings.Where(row => row.Component == HardwareType.Cpu && row.Type == SensorType.Load
        && row.Name.StartsWith("CPU Core", StringComparison.OrdinalIgnoreCase)).Max(row => row.Value);
    public double? CpuTemperature => Readings.Where(row => row.Component == HardwareType.Cpu && row.Type == SensorType.Temperature)
        .Max(row => row.Value);
    public double? CpuClock => Readings.Where(row => row.Component == HardwareType.Cpu && row.Type == SensorType.Clock &&
        row.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)).Max(row => row.Value);
    public double? CpuPower => Readings.Where(row => row.Component == HardwareType.Cpu && row.Type == SensorType.Power &&
        row.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)).Max(row => row.Value);
    public double? RamLoad => Find(HardwareType.Memory, SensorType.Load, "Memory");

    public static bool IsGraphics(HardwareType type) => type is HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.GpuNvidia;

    public SensorReading? CurrentGpu => Readings.Where(row => IsGraphics(row.Component) && row.Type == SensorType.Load
            && row.Name is ("GPU Core" or "GPU D3D 3D") && row.Value.HasValue)
        .MaxBy(row => row.Value);

    public double? GpuLoad => CurrentGpu?.Value ?? Readings.FirstOrDefault(row => row.Id == "windows.gpu")?.Value;
    public double? GpuTemperature => Readings.Where(row => IsGraphics(row.Component) && row.Type == SensorType.Temperature &&
        (CurrentGpu is null || row.DeviceId == CurrentGpu.DeviceId) &&
        row.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)).Max(row => row.Value);
    public double? GpuClock => Readings.Where(row => IsGraphics(row.Component) && row.Type == SensorType.Clock &&
        row.DeviceId == CurrentGpu?.DeviceId && row.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase)).Max(row => row.Value);
    public double? GpuPower => Readings.Where(row => IsGraphics(row.Component) && row.Type == SensorType.Power &&
        row.DeviceId == CurrentGpu?.DeviceId && row.Name is ("GPU Package" or "GPU Board Power" or "GPU Power")).Max(row => row.Value);

    public double? VramLoad
    {
        get
        {
            if (CurrentGpu is not { } gpu)
            {
                return null;
            }

            var memory = Readings.Where(row => row.DeviceId == gpu.DeviceId && row.Type is SensorType.Data or SensorType.SmallData);
            var used = memory.FirstOrDefault(row => row.Name == "GPU Memory Used");
            var total = memory.FirstOrDefault(row => row.Name == "GPU Memory Total");
            if (used?.Value is double usedValue && total?.Value is double totalValue && totalValue > 0 && used.Type == total.Type)
            {
                return Math.Clamp(100 * usedValue / totalValue, 0, 100);
            }

            return null;
        }
    }

    private double? Find(HardwareType component, SensorType type, string name) => Readings.FirstOrDefault(row =>
        row.Component == component && row.Type == type && row.Value.HasValue &&
        row.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
}

internal sealed class SensorSampler : IDisposable
{
    private Computer? computer;
    private PdhCounter? cpuCounter;
    private GpuTelemetry? gpuCounter;
    private bool initialized;

    public SensorSample Read()
    {
        if (!initialized)
        {
            Initialize();
        }

        var readings = new List<SensorReading>();
        if (computer is not null)
        {
            foreach (IHardware hardware in computer.Hardware)
            {
                ReadHardware(hardware, readings);
            }
        }

        AddWindowsFallbacks(readings);
        return new(readings, computer is not null);
    }

    private void Initialize()
    {
        initialized = true;
        var provider = new Computer
        {
            IsBatteryEnabled = true,
            IsControllerEnabled = true,
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsNetworkEnabled = true,
            IsPsuEnabled = true,
            IsStorageEnabled = true
        };

        try
        {
            provider.Open();
            computer = provider;
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            CloseProvider(provider);
        }
    }

    private static void ReadHardware(IHardware hardware, List<SensorReading> readings)
    {
        try
        {
            hardware.Update();
            string deviceId = hardware.Identifier.ToString();
            foreach (ISensor sensor in hardware.Sensors)
            {
                readings.Add(new(sensor.Identifier.ToString(), deviceId, hardware.Name, sensor.Name, hardware.HardwareType,
                    sensor.SensorType, Finite(sensor.Value), Finite(sensor.Min), Finite(sensor.Max)));
            }
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
        }

        foreach (IHardware child in hardware.SubHardware)
        {
            ReadHardware(child, readings);
        }
    }

    private void AddWindowsFallbacks(List<SensorReading> readings)
    {
        if (!readings.Any(row => row.Component == HardwareType.Cpu && row.Name == "CPU Total" && row.Value.HasValue))
        {
            cpuCounter ??= PdhCounter.TryOpen(@"\Processor(_Total)\% Processor Time");
            if (cpuCounter?.Collect() is { Count: > 0 } cpuReadings)
            {
                readings.Add(new("windows.cpu", "windows.cpu", "Processador · Windows", "CPU Total", HardwareType.Cpu,
                    SensorType.Load, Math.Clamp(cpuReadings[0].Value, 0, 100)));
            }
        }

        if (!readings.Any(row => SensorSample.IsGraphics(row.Component) && row.Type == SensorType.Load &&
            row.Name is ("GPU Core" or "GPU D3D 3D") && row.Value.HasValue))
        {
            gpuCounter ??= GpuTelemetry.Open();
            if (gpuCounter?.Read() is { } gpu)
            {
                readings.Add(new("windows.gpu", "windows.gpu", "GPU · Windows", "Motor gráfico mais ocupado", HardwareType.GpuIntel,
                    SensorType.Load, gpu.Utilization));
            }
        }

        if (!readings.Any(row => row.Component == HardwareType.Memory && row.Name == "Memory" && row.Value.HasValue))
        {
            var memory = new MemoryStatus { Size = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref memory))
            {
                readings.Add(new("windows.memory", "windows.memory", "RAM · Windows", "Memory", HardwareType.Memory,
                    SensorType.Load, memory.Load));
            }
        }
    }

    public void Dispose()
    {
        if (computer is not null)
        {
            CloseProvider(computer);
        }
        cpuCounter?.Dispose();
        gpuCounter?.Dispose();
    }

    private static double? Finite(float? value) => value.HasValue && float.IsFinite(value.Value) ? value.Value : null;

    private static void CloseProvider(Computer provider)
    {
        try
        {
            provider.Close();
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Size, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, ExtendedVirtual;
    }
}

internal static class SensorDisplay
{
    internal static string Category(SensorType type) => type switch
    {
        SensorType.Voltage => "Tensão", SensorType.Current => "Corrente", SensorType.Power => "Consumo",
        SensorType.Clock => "Clock", SensorType.Temperature => "Temperatura", SensorType.Load => "Carga",
        SensorType.Frequency => "Frequência", SensorType.Fan => "Ventoinha", SensorType.Flow => "Fluxo",
        SensorType.Control => "Controle", SensorType.Level => "Nível", SensorType.Factor => "Fator",
        SensorType.Data or SensorType.SmallData => "Dados", SensorType.Throughput => "Taxa",
        SensorType.TimeSpan or SensorType.Timing => "Tempo", SensorType.Energy => "Energia", SensorType.Noise => "Ruído",
        SensorType.Conductivity => "Condutividade", SensorType.Humidity => "Umidade", _ => type.ToString()
    };

    internal static string Value(SensorType type, double? reading)
    {
        if (reading is not double value)
        {
            return "Indisponível";
        }

        (string unit, string precision) = type switch
        {
            SensorType.Voltage => ("V", "0.###"), SensorType.Current => ("A", "0.###"),
            SensorType.Power => ("W", "0.#"), SensorType.Clock => ("MHz", "0.#"),
            SensorType.Temperature => ("°C", "0.#"), SensorType.Frequency => ("Hz", "0.#"),
            SensorType.Fan => ("RPM", "0"), SensorType.Flow => ("L/h", "0.#"),
            SensorType.Load or SensorType.Control or SensorType.Level or SensorType.Humidity => ("%", "0.#"),
            SensorType.Data => ("GiB", "0.##"), SensorType.SmallData => ("MiB", "0.##"),
            SensorType.Throughput => ("KiB/s", "0.##"), SensorType.TimeSpan => ("s", "0.#"),
            SensorType.Timing => ("ns", "0.##"), SensorType.Energy => ("mWh", "0.#"),
            SensorType.Noise => ("dBA", "0.#"), SensorType.Conductivity => ("µS/cm", "0.##"), _ => ("", "0.##")
        };
        if (type == SensorType.Throughput)
        {
            value /= 1024;
            if (value >= 1024)
            {
                value /= 1024;
                unit = "MiB/s";
            }
        }

        string number = value.ToString(precision, CultureInfo.GetCultureInfo("pt-BR"));
        return unit.Length > 0 ? $"{number} {unit}" : number;
    }
}
