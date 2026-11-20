using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace WPM.App.Diagnostics;

public static partial class DiagnosticCollector
{
    private const string Unavailable = "Indisponível";
    private const string PartialData = "Alguns dados não foram disponibilizados pelo Windows.";

    public static DiagnosticReport Collect()
    {
        return new DiagnosticReport(DateTimeOffset.Now,
        [
            CollectWindows(),
            CollectProcessors(),
            CollectMotherboard(),
            ..CollectMemory(),
            ..GraphicsDiagnostics.Collect().Select(section => section with { Category = DiagnosticCategory.Graphics }),
            ..CollectVolumes().Select(section => section with { Category = DiagnosticCategory.Storage }),
            ..CollectPhysicalDisks().Select(section => section with { Category = DiagnosticCategory.Storage }),
            PowerDiagnostics.Collect() with { Category = DiagnosticCategory.Power },
            ..NetworkDiagnostics.Collect().Select(section => section with { Category = DiagnosticCategory.Network }),
            TemperatureDiagnostics.Collect(),
            ..PeripheralDiagnostics.Collect().Select(section => section with { Category = DiagnosticCategory.Devices })
        ]);
    }

    private static DiagnosticSection CollectWindows()
    {
        string? edition = null;
        string? installed = null;
        string? lastBoot = null;
        string? directory = null;
        string? issue = WmiDiagnosticQuery.Read("SELECT Caption, InstallDate, LastBootUpTime, WindowsDirectory FROM Win32_OperatingSystem",
            row =>
            {
                edition ??= ReadText(row["Caption"]);
                installed ??= FormatWindowsDate(row["InstallDate"]);
                lastBoot ??= FormatWindowsDate(row["LastBootUpTime"]);
                directory ??= ReadText(row["WindowsDirectory"]);
            });

        Version version = Environment.OSVersion.Version;
        string displayVersion = $"{version.Major}.{version.Minor}";
        string build = $"{version.Build}";

        ReadRegistry(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", key =>
        {
            edition ??= ReadText(key.GetValue("EditionID"));
            displayVersion = ReadText(key.GetValue("DisplayVersion")) ?? displayVersion;
            if (key.GetValue("UBR") is int revision and >= 0)
            {
                build = $"{build}.{revision}";
            }
        });

        return CreateSection("Windows",
        [
            new("Edição", edition ?? Unavailable),
            new("Versão", displayVersion),
            new("Build", build),
            new("Arquitetura do sistema", RuntimeInformation.OSArchitecture.ToString()),
            new("Data de instalação", installed ?? Unavailable),
            new("Última inicialização", lastBoot ?? Unavailable),
            new("Pasta do Windows", directory ?? Unavailable)
        ], issue);
    }

    private static string? FormatWindowsDate(object? value)
    {
        if (value is not string date)
        {
            return null;
        }

        try
        {
            return ManagementDateTimeConverter.ToDateTime(date).ToString("dd/MM/yyyy HH:mm");
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static DiagnosticSection CollectProcessors()
    {
        var items = new List<DiagnosticItem>();
        int processor = 0;

        string? issue = WmiDiagnosticQuery.Read(
            "SELECT Name, Manufacturer, SocketDesignation, NumberOfCores, NumberOfEnabledCore, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize FROM Win32_Processor", row =>
            {
                string label = $"Processador {++processor}";
                items.Add(new($"{label} · modelo", ReadText(row["Name"]) ?? Unavailable));
                items.Add(new($"{label} · fabricante", ReadText(row["Manufacturer"]) ?? Unavailable));
                items.Add(new($"{label} · soquete", ReadText(row["SocketDesignation"]) ?? Unavailable));
                items.Add(new($"{label} · núcleos", ReadPositiveCount(row["NumberOfCores"])));
                items.Add(new($"{label} · threads (processadores lógicos)", ReadPositiveCount(row["NumberOfLogicalProcessors"])));
                AddProcessorMetric(items, label, "núcleos habilitados", row["NumberOfEnabledCore"]);
                AddProcessorMetric(items, label, "frequência informada pelo firmware", row["MaxClockSpeed"], " MHz");
                AddProcessorMetric(items, label, "cache L2", row["L2CacheSize"], " KiB");
                AddProcessorMetric(items, label, "cache L3", row["L3CacheSize"], " KiB");
            });

        if (items.Count == 0)
        {
            string? model = null;
            ReadRegistry(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                key => model = ReadText(key.GetValue("ProcessorNameString")));

            items.Add(new("Modelo do primeiro processador", model ?? Unavailable));
            items.Add(new("Núcleos", Unavailable));
            items.Add(new("Processadores lógicos disponíveis ao aplicativo", Environment.ProcessorCount.ToString()));
        }

        return CreateSection("Processador", items, issue);
    }

    private static void AddProcessorMetric(List<DiagnosticItem> items, string processor, string label, object? value, string unit = "")
    {
        if (value is uint amount and > 0)
        {
            items.Add(new($"{processor} · {label}", $"{amount}{unit}"));
        }
    }

    private static IReadOnlyList<DiagnosticSection> CollectVolumes()
    {
        var sections = new List<DiagnosticSection>();
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                    {
                        continue;
                    }

                    string name = $"Volume {drive.Name.TrimEnd('\\')}";
                    if (!drive.IsReady)
                    {
                        sections.Add(new(name, [], "O volume não está montado."));
                        continue;
                    }

                    long total = drive.TotalSize;
                    long free = drive.TotalFreeSpace;
                    long used = Math.Max(total - free, 0);
                    string occupancy = total > 0 ? $"{(double)used / total:P1}" : Unavailable;
                    sections.Add(CreateSection(name,
                    [
                        new("Rótulo", string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Sem rótulo" : drive.VolumeLabel),
                        new("Sistema de arquivos", drive.DriveFormat),
                        new("Capacidade total", FormatSize(total)),
                        new("Espaço em uso", $"{FormatSize(used)} · {occupancy}"),
                        new("Espaço livre", FormatSize(free))
                    ], null));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
                {
                    sections.Add(new($"Volume {drive.Name.TrimEnd('\\')}", [], "O Windows não permitiu consultar este volume."));
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            sections.Add(new("Volumes", [], "O Windows não permitiu consultar os volumes."));
        }

        return sections.Count > 0 ? sections : [new("Volumes", [], "Nenhum volume local foi identificado.")];
    }

    private static DiagnosticSection CreateSection(string title, IReadOnlyList<DiagnosticItem> items, string? issue)
    {
        issue ??= items.Count == 0 || items.Any(item => item.Value == Unavailable) ? PartialData : null;
        return new DiagnosticSection(title, items, issue);
    }

    private static void ReadRegistry(string path, Action<RegistryKey> consume)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(path, writable: false);
            if (key is not null)
            {
                consume(key);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            // O Windows pode restringir a leitura de uma chave. A informação continua opcional.
        }
    }

    private static string? ReadText(object? value)
    {
        return value is string text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
    }

    private static string ReadPositiveCount(object? value)
    {
        return value is uint count and > 0 ? count.ToString() : Unavailable;
    }

    private static string FormatSize(double bytes)
    {
        const double GiB = 1024d * 1024 * 1024;
        const double TiB = GiB * 1024;
        return bytes >= TiB ? $"{bytes / TiB:0.##} TiB" : $"{bytes / GiB:0.##} GiB";
    }

}
