using System.Runtime.InteropServices;

namespace WPM.App.Diagnostics;

public static partial class DiagnosticCollector
{
    private static IReadOnlyList<DiagnosticSection> CollectMemory()
    {
        var modules = new List<DiagnosticSection>();
        var types = new HashSet<string>(StringComparer.Ordinal);
        ulong moduleBytes = 0;
        string? moduleIssue = WmiDiagnosticQuery.Read(
            "SELECT Capacity, DeviceLocator, BankLabel, Manufacturer, PartNumber, SMBIOSMemoryType, FormFactor, Speed, ConfiguredClockSpeed, ConfiguredVoltage, DataWidth, TotalWidth FROM Win32_PhysicalMemory", row =>
            {
                if (row["Capacity"] is not ulong capacity || capacity == 0 || capacity == ulong.MaxValue)
                {
                    return;
                }

                moduleBytes += capacity;
                string? location = ReadFirmwareText(row["DeviceLocator"]);
                string? type = GetMemoryType(row["SMBIOSMemoryType"]);
                if (type is not null)
                {
                    types.Add(type);
                }

                var items = new List<DiagnosticItem>
                {
                    new("Capacidade", FormatSize(capacity)),
                    new("Slot", location ?? Unavailable),
                    new("Banco", ReadFirmwareText(row["BankLabel"]) ?? Unavailable),
                    new("Fabricante", ReadFirmwareText(row["Manufacturer"]) ?? Unavailable),
                    new("Código do módulo", ReadFirmwareText(row["PartNumber"]) ?? Unavailable),
                    new("Tipo de memória", type ?? Unavailable),
                    new("Formato físico", GetMemoryFormFactor(row["FormFactor"]) ?? Unavailable),
                    new("Velocidade nominal", GetMemorySpeed(row["Speed"])),
                    new("Velocidade configurada", GetMemorySpeed(row["ConfiguredClockSpeed"]))
                };
                if (row["ConfiguredVoltage"] is uint voltage and > 0)
                {
                    items.Add(new("Tensão configurada", $"{voltage / 1000d:0.###} V"));
                }

                if (row["DataWidth"] is ushort dataWidth and > 0 and < ushort.MaxValue)
                {
                    items.Add(new("Largura de dados", $"{dataWidth} bits"));
                }

                if (row["TotalWidth"] is ushort totalWidth and > 0 and < ushort.MaxValue)
                {
                    items.Add(new("Largura total do módulo", $"{totalWidth} bits"));
                }

                modules.Add(CreateSection($"Módulo de RAM {modules.Count + 1}", items, null));
            });

        int arrayCount = 0;
        int slotCount = 0;
        double maxCapacityKiB = 0;
        bool hasAllCapacity = true;
        bool hasAllSlots = true;
        string? arrayIssue = WmiDiagnosticQuery.Read(
            "SELECT MemoryDevices, MaxCapacity, MaxCapacityEx FROM Win32_PhysicalMemoryArray WHERE Use = 3", row =>
            {
                arrayCount++;
                if (row["MemoryDevices"] is ushort slots and > 0 and < ushort.MaxValue)
                {
                    slotCount += slots;
                }
                else
                {
                    hasAllSlots = false;
                }

                if (row["MaxCapacityEx"] is ulong extended and > 0 and < ulong.MaxValue)
                {
                    maxCapacityKiB += extended;
                }
                else if (row["MaxCapacity"] is uint legacy and > 0 and < 0x80000000)
                {
                    maxCapacityKiB += legacy;
                }
                else
                {
                    hasAllCapacity = false;
                }
            });

        double installed = GetPhysicallyInstalledSystemMemory(out ulong installedKiB) && installedKiB > 0
            ? installedKiB * 1024d
            : moduleBytes;
        double maxBytes = maxCapacityKiB * 1024d;
        DiagnosticSection summary = CreateSection("Memória RAM",
        [
            new("Total instalado", installed > 0 ? FormatSize(installed) : Unavailable),
            new("Módulos identificados", modules.Count > 0 ? modules.Count.ToString() : Unavailable),
            new("Posições informadas pelo firmware", arrayIssue is null && arrayCount > 0 && hasAllSlots && slotCount >= modules.Count
                ? slotCount.ToString() : Unavailable),
            new("Capacidade máxima informada pelo firmware", arrayIssue is null && arrayCount > 0 && hasAllCapacity
                && maxBytes > 0 && maxBytes >= installed ? FormatSize(maxBytes) : Unavailable),
            new("Tecnologias identificadas", types.Count > 0 ? string.Join(", ", types.Order()) : Unavailable)
        ], moduleIssue ?? arrayIssue);

        return [summary, ..modules];
    }

    private static string GetMemorySpeed(object? value) => value is uint speed and > 0 and < 65535
        ? $"{speed} MT/s"
        : Unavailable;

    private static string? GetMemoryType(object? value) => value switch
    {
        (uint)15 => "SDRAM",
        (uint)17 => "RDRAM",
        (uint)18 => "DDR",
        (uint)19 => "DDR2",
        (uint)20 => "DDR2 FB-DIMM",
        (uint)24 => "DDR3",
        (uint)26 => "DDR4",
        (uint)27 => "LPDDR",
        (uint)28 => "LPDDR2",
        (uint)29 => "LPDDR3",
        (uint)30 => "LPDDR4",
        (uint)34 => "DDR5",
        (uint)35 => "LPDDR5",
        _ => null
    };

    private static string? GetMemoryFormFactor(object? value) => value switch
    {
        (ushort)5 => "SIMM",
        (ushort)8 => "DIMM",
        (ushort)12 => "SO-DIMM",
        (ushort)14 => "SMD",
        (ushort)21 => "BGA",
        _ => null
    };

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);
}
