using System.Runtime.InteropServices;
using System.Text;
using static WPM.App.Diagnostics.TemperatureDiagnostics;

namespace WPM.App.Diagnostics;

internal static class NvidiaDiagnostics
{
    private const int Success = 0;
    private const uint GpuTemperature = 0;
    private const uint GraphicsClock = 0;
    private const uint MemoryClock = 2;

    public static IReadOnlyList<DiagnosticSection> Collect()
    {
        nint library = LoadNvml();
        if (library == 0)
        {
            return [];
        }

        try
        {
            if (GetExport<Command>(library, "nvmlInit_v2") is not { } initialize ||
                GetExport<Command>(library, "nvmlShutdown") is not { } shutdown ||
                GetExport<DeviceCount>(library, "nvmlDeviceGetCount_v2") is not { } getCount ||
                GetExport<GetDevice>(library, "nvmlDeviceGetHandleByIndex_v2") is not { } getDevice ||
                initialize() != Success)
            {
                return [];
            }

            try
            {
                if (getCount(out uint count) != Success)
                {
                    return [];
                }

                var versionBuffer = new StringBuilder(128);
                var getVersion = GetExport<SystemString>(library, "nvmlSystemGetDriverVersion");
                string? driverVersion = getVersion?.Invoke(versionBuffer, (uint)versionBuffer.Capacity) == Success
                    ? versionBuffer.ToString().Trim()
                    : null;

                var sections = new List<DiagnosticSection>();
                for (uint index = 0; index < count; index++)
                {
                    if (getDevice(index, out nint device) == Success && device != 0)
                    {
                        sections.Add(BuildSection(library, device, index + 1, driverVersion));
                    }
                }

                return sections;
            }
            finally
            {
                shutdown();
            }
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    private static DiagnosticSection BuildSection(nint library, nint device, uint index, string? driverVersion)
    {
        string? name = ReadString(library, device, "nvmlDeviceGetName");
        var items = new List<DiagnosticItem>();
        Add(items, "Modelo", name);
        Add(items, "Driver NVIDIA", driverVersion);
        Add(items, "Arquitetura", GetArchitecture(Read(library, device, "nvmlDeviceGetArchitecture")));
        Add(items, "VBIOS", ReadString(library, device, "nvmlDeviceGetVbiosVersion"));
        uint? cores = Read(library, device, "nvmlDeviceGetNumGpuCores");
        items.Add(new("Núcleos CUDA", cores is > 0 ? $"{cores:N0}" : "Indisponível"));

        var getCapability = GetExport<GetCudaCapability>(library, "nvmlDeviceGetCudaComputeCapability");
        if (getCapability is not null && getCapability(device, out int major, out int minor) == Success && major > 0)
        {
            items.Add(new("Capacidade de computação CUDA", $"{major}.{minor}"));
        }

        Add(items, "Largura do barramento da VRAM", Read(library, device, "nvmlDeviceGetMemoryBusWidth"), " bits");
        AddPcie(items, "PCI Express em uso", library, device, "nvmlDeviceGetCurrPcieLinkGeneration", "nvmlDeviceGetCurrPcieLinkWidth");
        AddPcie(items, "Limite de PCI Express informado para esta conexão", library, device,
            "nvmlDeviceGetMaxPcieLinkGeneration", "nvmlDeviceGetMaxPcieLinkWidth");
        if (Read(library, device, "nvmlDeviceGetGpuMaxPcieLinkGeneration") is > 0 and uint generation)
        {
            items.Add(new("Geração PCI Express suportada pela GPU", $"{generation}"));
        }

        var getMemory = GetExport<ReadMemory>(library, "nvmlDeviceGetMemoryInfo");
        if (getMemory is not null && getMemory(device, out var memory) == Success && memory.Total > 0)
        {
            const double bytesPerGiB = 1024d * 1024 * 1024;
            items.Add(new("VRAM usada na coleta", $"{memory.Used / bytesPerGiB:0.##} GiB"));
            items.Add(new("VRAM livre na coleta", $"{memory.Free / bytesPerGiB:0.##} GiB"));
        }

        var getUtilization = GetExport<ReadUtilization>(library, "nvmlDeviceGetUtilizationRates");
        if (getUtilization is not null && getUtilization(device, out var utilization) == Success)
        {
            Add(items, "Carga da GPU na coleta", utilization.Gpu, "%");
            Add(items, "Atividade da memória na coleta", utilization.Memory, "%");
        }

        uint? temperature = Read(library, device, "nvmlDeviceGetTemperature", GpuTemperature);
        if (temperature is <= 200)
        {
            Add(items, "Temperatura na coleta", temperature, " °C");
        }

        Add(items, "Clock gráfico na coleta", Read(library, device, "nvmlDeviceGetClockInfo", GraphicsClock), " MHz");
        Add(items, "Clock da memória na coleta", Read(library, device, "nvmlDeviceGetClockInfo", MemoryClock), " MHz");
        uint? power = Read(library, device, "nvmlDeviceGetPowerUsage");
        if (power.HasValue)
        {
            items.Add(new("Consumo informado pela GPU na coleta", $"{power.Value / 1000d:0.#} W"));
        }

        return new($"Dados NVIDIA · GPU {index}", items);
    }

    private static string? GetArchitecture(uint? architecture) => architecture switch
    {
        2 => "Kepler",
        3 => "Maxwell",
        4 => "Pascal",
        5 => "Volta",
        6 => "Turing",
        7 => "Ampere",
        8 => "Ada Lovelace",
        9 => "Hopper",
        10 => "Blackwell",
        _ => null
    };

    private static void AddPcie(List<DiagnosticItem> items, string label, nint library, nint device, string getGeneration, string getWidth)
    {
        uint? generation = Read(library, device, getGeneration);
        uint? width = Read(library, device, getWidth);
        if (generation is > 0 && width is > 0)
        {
            items.Add(new(label, $"Geração {generation} ×{width}"));
        }
    }

    private static void Add(List<DiagnosticItem> items, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            items.Add(new(label, value));
        }
    }

    private static void Add(List<DiagnosticItem> items, string label, uint? value, string unit)
    {
        if (value.HasValue)
        {
            items.Add(new(label, $"{value}{unit}"));
        }
    }

    private static string? ReadString(nint library, nint device, string method)
    {
        var buffer = new StringBuilder(128);
        var get = GetExport<DeviceString>(library, method);
        return get?.Invoke(device, buffer, (uint)buffer.Capacity) == Success ? buffer.ToString().Trim() : null;
    }

    private static uint? Read(nint library, nint device, string method)
    {
        var get = GetExport<ReadMetric>(library, method);
        return get is not null && get(device, out uint value) == Success ? value : null;
    }

    private static uint? Read(nint library, nint device, string method, uint argument)
    {
        var get = GetExport<ReadTypedMetric>(library, method);
        return get is not null && get(device, argument, out uint value) == Success ? value : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Memory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Command();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DeviceCount(out uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDevice(uint index, out nint device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate int SystemString([Out] StringBuilder value, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate int DeviceString(nint device, [Out] StringBuilder value, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadMetric(nint device, out uint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadTypedMetric(nint device, uint type, out uint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetCudaCapability(nint device, out int major, out int minor);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadUtilization(nint device, out Utilization value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadMemory(nint device, out Memory value);
}
