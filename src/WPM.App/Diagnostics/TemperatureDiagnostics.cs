using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WPM.App.Diagnostics;

internal static class TemperatureDiagnostics
{
    private const int NvmlSuccess = 0;
    private const int NvmlGpuSensor = 0;
    private const uint SearchLibraryDirectory = 0x00000100;
    private const uint SearchSystem32 = 0x00000800;
    private const string Unavailable = "Indisponível";

    internal static DiagnosticSection Collect()
    {
        var items = new List<DiagnosticItem>();
        ReadThermalZones(items);
        bool hasThermalZones = items.Count > 0;
        if (!hasThermalZones)
        {
            items.Add(new("Zonas térmicas ACPI", Unavailable));
        }

        int countBeforeGpu = items.Count;
        ReadNvidiaTemperatures(items);
        bool hasGpuTemperature = items.Count > countBeforeGpu;
        if (!hasGpuTemperature)
        {
            items.Add(new("Sensor da GPU NVIDIA", Unavailable));
        }

        string? issue = hasThermalZones || hasGpuTemperature
            ? null
            : "O firmware e o driver de vídeo não forneceram leituras térmicas.";
        return new DiagnosticSection("Temperaturas", items.ToArray(), issue);
    }

    private static void ReadThermalZones(List<DiagnosticItem> items)
    {
        WmiDiagnosticQuery.Read(
            "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature WHERE Active = TRUE",
            row =>
            {
                // The ACPI WMI provider uses tenths of a kelvin.
                if (row["CurrentTemperature"] is uint temperature)
                {
                    AddThermalZone(items, row["InstanceName"], temperature / 10m, "0.0");
                }
            }, @"root\WMI");

        if (items.Count == 0)
        {
            // The Windows thermal-zone Temperature counter uses whole kelvins.
            WmiDiagnosticQuery.Read(
                "SELECT Name, Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation",
                row =>
                {
                    if (row["Temperature"] is uint temperature && row["Name"] as string != "_Total")
                    {
                        AddThermalZone(items, row["Name"], temperature, "0");
                    }
                });
        }
    }

    private static void AddThermalZone(List<DiagnosticItem> items, object? name, decimal kelvin, string format)
    {
        // Ignore missing values and readings outside any plausible PC operating temperature.
        if (kelvin is >= 150m and <= 500m)
        {
            string zone = name is string text && !string.IsNullOrWhiteSpace(text)
                ? text.Trim()
                : (items.Count + 1).ToString();
            decimal celsius = kelvin - 273.15m;
            items.Add(new($"Zona térmica ACPI · {zone}", $"{celsius.ToString(format)} °C"));
        }
    }

    private static void ReadNvidiaTemperatures(List<DiagnosticItem> items)
    {
        nint library = LoadNvml();
        if (library == 0)
        {
            return;
        }

        try
        {
            if (GetExport<NvmlOperation>(library, "nvmlInit_v2") is not { } initialize ||
                GetExport<NvmlOperation>(library, "nvmlShutdown") is not { } shutdown ||
                GetExport<NvmlDeviceCount>(library, "nvmlDeviceGetCount_v2") is not { } getDeviceCount ||
                GetExport<NvmlDeviceHandle>(library, "nvmlDeviceGetHandleByIndex_v2") is not { } getDeviceHandle ||
                GetExport<NvmlTemperature>(library, "nvmlDeviceGetTemperature") is not { } getTemperature ||
                initialize() != NvmlSuccess)
            {
                return;
            }

            try
            {
                if (getDeviceCount(out uint deviceCount) != NvmlSuccess)
                {
                    return;
                }

                var getName = GetExport<NvmlDeviceName>(library, "nvmlDeviceGetName");
                for (uint index = 0; index < deviceCount; index++)
                {
                    if (getDeviceHandle(index, out nint device) != NvmlSuccess ||
                        device == 0 ||
                        getTemperature(device, NvmlGpuSensor, out uint temperature) != NvmlSuccess ||
                        temperature > 200)
                    {
                        continue;
                    }

                    string label = $"GPU {index + 1}";
                    var nameBuffer = new StringBuilder(128);
                    if (getName is not null && getName(device, nameBuffer, (uint)nameBuffer.Capacity) == NvmlSuccess)
                    {
                        string name = nameBuffer.ToString().Trim();
                        if (name.Length > 0)
                        {
                            label += $" · {name}";
                        }
                    }

                    items.Add(new(label, $"{temperature} °C"));
                }
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

    internal static nint LoadNvml()
    {
        // NVIDIA documents System32 for DCH and Program Files/NVIDIA Corporation/NVSMI for Standard.
        // For both paths, restrict dependency loading to the same protected folder and System32.
        const uint searchFlags = SearchLibraryDirectory | SearchSystem32;
        nint library = LoadLibraryEx(Path.Combine(Environment.SystemDirectory, "nvml.dll"), 0, searchFlags);
        if (library != 0)
        {
            return library;
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return Path.IsPathFullyQualified(programFiles)
            ? LoadLibraryEx(Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvml.dll"), 0, searchFlags)
            : 0;
    }

    internal static T? GetExport<T>(nint library, string name) where T : Delegate
    {
        return NativeLibrary.TryGetExport(library, name, out nint method)
            ? Marshal.GetDelegateForFunctionPointer<T>(method)
            : null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlOperation();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceCount(out uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceHandle(uint index, out nint device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlTemperature(nint device, int sensor, out uint temperature);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceName(nint device, [Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder name, uint length);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint LoadLibraryEx(string fileName, nint file, uint flags);
}
