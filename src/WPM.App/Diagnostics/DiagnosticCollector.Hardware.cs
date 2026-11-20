using System.Globalization;
using System.Runtime.InteropServices;

namespace WPM.App.Diagnostics;

public static partial class DiagnosticCollector
{
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";

    private static DiagnosticSection CollectMotherboard()
    {
        string? manufacturer = null;
        string? model = null;
        string? revision = null;
        string? firmwareManufacturer = null;
        string? firmwareVersion = null;
        string? firmwareDate = null;
        string? smbiosVersion = null;

        string? boardIssue = WmiDiagnosticQuery.Read("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard", row =>
        {
            manufacturer ??= ReadFirmwareText(row["Manufacturer"]);
            model ??= ReadFirmwareText(row["Product"]);
            revision ??= ReadFirmwareText(row["Version"]);
        });
        string? firmwareIssue = WmiDiagnosticQuery.Read(
            "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SMBIOSMajorVersion, SMBIOSMinorVersion FROM Win32_BIOS WHERE PrimaryBIOS = TRUE", row =>
        {
            firmwareManufacturer ??= ReadFirmwareText(row["Manufacturer"]);
            firmwareVersion ??= ReadFirmwareText(row["SMBIOSBIOSVersion"]);
            firmwareDate ??= FormatFirmwareDate(row["ReleaseDate"]);
            if (row["SMBIOSMajorVersion"] is ushort major and > 0 && row["SMBIOSMinorVersion"] is ushort minor)
            {
                smbiosVersion ??= $"{major}.{minor}";
            }
        });

        ReadRegistry(@"HARDWARE\DESCRIPTION\System\BIOS", key =>
        {
            manufacturer ??= ReadFirmwareText(key.GetValue("BaseBoardManufacturer"));
            model ??= ReadFirmwareText(key.GetValue("BaseBoardProduct"));
            revision ??= ReadFirmwareText(key.GetValue("BaseBoardVersion"));
            firmwareManufacturer ??= ReadFirmwareText(key.GetValue("BIOSVendor"));
            firmwareVersion ??= ReadFirmwareText(key.GetValue("BIOSVersion"));
        });

        var items = new List<DiagnosticItem>
        {
            new("Fabricante da placa-mãe", manufacturer ?? Unavailable),
            new("Modelo da placa-mãe", model ?? Unavailable),
            new("Fabricante do firmware", firmwareManufacturer ?? Unavailable),
            new("Versão do firmware", firmwareVersion ?? Unavailable),
            new("Data do firmware", firmwareDate ?? Unavailable)
        };
        if (revision is not null)
        {
            items.Insert(2, new("Revisão da placa-mãe", revision));
        }

        if (smbiosVersion is not null)
        {
            items.Add(new("Versão SMBIOS", smbiosVersion));
        }

        if (GetFirmwareType(out uint firmware))
        {
            items.Add(new("Modo de inicialização", firmware switch { 1 => "BIOS legado", 2 => "UEFI", _ => Unavailable }));
        }

        return CreateSection("Placa-mãe e BIOS", items, boardIssue ?? firmwareIssue);
    }

    private static string? ReadFirmwareText(object? value)
    {
        string? text = ReadText(value);
        return text?.ToUpperInvariant() is "DEFAULT STRING" or "TO BE FILLED BY O.E.M." or "SYSTEM PRODUCT NAME"
            or "SYSTEM MANUFACTURER" or "SYSTEM VERSION" or "NOT SPECIFIED" ? null : text;
    }

    private static string? FormatFirmwareDate(object? value)
    {
        if (value is string date && date.Length >= 8 &&
            DateOnly.TryParseExact(date.AsSpan(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsedDate))
        {
            return parsedDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static IReadOnlyList<DiagnosticSection> CollectPhysicalDisks()
    {
        var disks = new List<PhysicalDisk>();
        string? issue = WmiDiagnosticQuery.Read(
            "SELECT DeviceId, Model, FriendlyName, Size, MediaType, BusType, HealthStatus, FirmwareVersion, SerialNumber FROM MSFT_PhysicalDisk",
            row => disks.Add(new(
                ReadText(row["DeviceId"]),
                ReadText(row["Model"]) ?? ReadText(row["FriendlyName"]),
                FormatDiskSize(row["Size"]),
                row["MediaType"] switch { (ushort)3 => "HDD", (ushort)4 => "SSD", (ushort)5 => "SCM", _ => null },
                FormatBusType(row["BusType"]),
                row["HealthStatus"] switch { (ushort)0 => "Saudável", (ushort)1 => "Atenção", (ushort)2 => "Com falha", _ => null },
                ReadText(row["FirmwareVersion"]),
                ReadText(row["SerialNumber"]))),
            StorageNamespace);

        var counters = new Dictionary<string, StorageReliability>(StringComparer.OrdinalIgnoreCase);
        if (disks.Count > 0)
        {
            counters = CollectDiskReliability();
        }
        else
        {
            issue = WmiDiagnosticQuery.Read("SELECT Model, Size, MediaType, InterfaceType, FirmwareRevision FROM Win32_DiskDrive",
                row => disks.Add(new(null,
                    ReadText(row["Model"]),
                    FormatDiskSize(row["Size"]),
                    FormatWin32DiskMedia(row["MediaType"]),
                    ReadText(row["InterfaceType"]),
                    null,
                    ReadText(row["FirmwareRevision"]),
                    null)));
        }

        if (disks.Count == 0)
        {
            return [new("Discos físicos", [], issue ?? "Nenhum disco físico foi identificado pelo Windows.")];
        }

        var sections = new List<DiagnosticSection>(disks.Count);
        var disksById = disks.ToLookup(disk => disk.DeviceId, StringComparer.OrdinalIgnoreCase);
        var nvmeDevices = disks.Any(disk => disk.Interface == "NVMe") ? ReadNvmeDevices() : [];
        var disksBySerial = disks.ToLookup(disk => disk.Serial, StringComparer.OrdinalIgnoreCase);
        foreach (PhysicalDisk disk in disks)
        {
            StorageReliability? reliability = disk.DeviceId is { } id && disksById[id].Count() == 1
                ? counters.GetValueOrDefault(id)
                : null;
            if (disk.Interface == "NVMe" && disk.Serial is { } serial && disksBySerial[serial].Count() == 1 &&
                nvmeDevices.TryGetValue(serial, out uint diskNumber))
            {
                StorageReliability? nvme = NvmeDiagnostics.Collect(diskNumber);
                if (nvme is not null)
                {
                    reliability = new(nvme.Temperature ?? reliability?.Temperature, nvme.Wear ?? reliability?.Wear,
                        nvme.Hours ?? reliability?.Hours, nvme.Cycles ?? reliability?.Cycles, reliability?.ReadErrors,
                        reliability?.WriteErrors, nvme.MediaErrors, nvme.WarningFlags);
                }
            }

            var items = new List<DiagnosticItem>
            {
                new("Modelo", disk.Model ?? Unavailable),
                new("Capacidade", disk.Size ?? Unavailable),
                new("Tipo de mídia", disk.Media ?? Unavailable),
                new("Interface", disk.Interface ?? Unavailable),
                new("Firmware", disk.Firmware ?? Unavailable),
                new("Saúde informada pelo Windows", disk.Health ?? Unavailable),
                new("Temperatura do disco", reliability?.Temperature is { } temperature ? $"{temperature} °C" : Unavailable)
            };
            if (disk.Media == "SSD" || disk.Interface == "NVMe")
            {
                items.Add(new("Vida útil estimada restante", reliability?.Wear is { } wear ? $"{Math.Max(0, 100 - wear)}%" : Unavailable));
            }

            AddDiskCounter(items, "Horas ligado", reliability?.Hours);
            AddDiskCounter(items, "Ciclos de energia ou partidas", reliability?.Cycles);
            AddDiskCounter(items, "Erros de leitura não corrigidos", reliability?.ReadErrors);
            AddDiskCounter(items, "Erros de gravação não corrigidos", reliability?.WriteErrors);
            AddDiskCounter(items, "Erros de mídia e integridade (NVMe)", reliability?.MediaErrors);
            if (reliability?.WarningFlags is { } warnings)
            {
                items.Add(new("Alertas críticos NVMe", warnings == 0 ? "Nenhum" : $"Ativos · 0x{warnings:X2}"));
            }

            sections.Add(CreateSection($"Disco físico {sections.Count + 1}", items, issue));
        }

        return sections;
    }

    private static Dictionary<string, StorageReliability> CollectDiskReliability()
    {
        var counters = new Dictionary<string, StorageReliability>(StringComparer.OrdinalIgnoreCase);
        var reportedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        WmiDiagnosticQuery.Read("SELECT DeviceId, Temperature, Wear, PowerOnHours, StartStopCycleCount, ReadErrorsUncorrected, WriteErrorsUncorrected FROM MSFT_StorageReliabilityCounter", row =>
        {
            if (ReadText(row["DeviceId"]) is not { } id)
            {
                return;
            }

            if (!reportedIds.Add(id))
            {
                // Alguns provedores retornam mais de um contador com o mesmo identificador.
                counters.Remove(id);
            }
            else
            {
                counters.Add(id, new(
                    row["Temperature"] is byte degrees and > 0 and < byte.MaxValue ? degrees : null,
                    row["Wear"] is byte wear and < byte.MaxValue ? wear : null,
                    ReadCounter(row["PowerOnHours"]),
                    ReadCounter(row["StartStopCycleCount"]),
                    ReadCounter(row["ReadErrorsUncorrected"]),
                    ReadCounter(row["WriteErrorsUncorrected"])));
            }
        }, StorageNamespace);
        return counters;
    }

    private static Dictionary<string, uint> ReadNvmeDevices()
    {
        var devices = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        WmiDiagnosticQuery.Read("SELECT Number, SerialNumber FROM MSFT_Disk WHERE BusType = 17", row =>
        {
            if (ReadText(row["SerialNumber"]) is { } serial && row["Number"] is uint number && !devices.TryAdd(serial, number))
            {
                ambiguous.Add(serial);
            }
        }, StorageNamespace);
        foreach (string serial in ambiguous)
        {
            devices.Remove(serial);
        }

        return devices;
    }

    private static ulong? ReadCounter(object? value) => value switch
    {
        byte number when number < byte.MaxValue => number,
        ushort number when number < ushort.MaxValue => number,
        uint number when number < uint.MaxValue => number,
        ulong number when number < ulong.MaxValue => number,
        _ => null
    };

    private static void AddDiskCounter(List<DiagnosticItem> items, string label, ulong? value)
    {
        if (value.HasValue)
        {
            items.Add(new(label, $"{value.Value:N0}"));
        }
    }

    private static string? FormatDiskSize(object? size) => size is ulong bytes and > 0 ? FormatSize(bytes) : null;

    private static string? FormatBusType(object? bus) => bus switch
    {
        (ushort)1 => "SCSI",
        (ushort)2 => "ATAPI",
        (ushort)3 => "ATA",
        (ushort)4 => "IEEE 1394",
        (ushort)5 => "SSA",
        (ushort)6 => "Fibre Channel",
        (ushort)7 => "USB",
        (ushort)8 => "RAID",
        (ushort)9 => "iSCSI",
        (ushort)10 => "SAS",
        (ushort)11 => "SATA",
        (ushort)12 => "SD",
        (ushort)13 => "MMC",
        (ushort)15 => "Disco virtual",
        (ushort)16 => "Espaços de Armazenamento",
        (ushort)17 => "NVMe",
        _ => null
    };

    private static string? FormatWin32DiskMedia(object? media) => ReadText(media) switch
    {
        "External hard disk media" => "Disco externo",
        "Removable media" => "Mídia removível",
        "Fixed hard disk media" => "Disco fixo",
        "Format unknown" => null,
        var description => description
    };

    private sealed record PhysicalDisk(string? DeviceId, string? Model, string? Size, string? Media, string? Interface,
        string? Health, string? Firmware, string? Serial);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out uint firmwareType);
}
