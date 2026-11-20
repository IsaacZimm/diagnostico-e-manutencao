using System.Runtime.InteropServices;
using System.Text;

namespace WPM.App.Diagnostics;

internal static class PowerDiagnostics
{
    private const string Unavailable = "Indisponível";
    private const string BatteryNamespace = @"root\WMI";
    private const uint ErrorMoreData = 234;
    private const uint CapacityRelative = 0x40000000;

    public static DiagnosticSection Collect()
    {
        var items = new List<DiagnosticItem>
        {
            new("Plano de energia ativo", GetPowerPlanName() ?? Unavailable)
        };
        if (!GetSystemPowerStatus(out var status))
        {
            return new("Energia", items, "O Windows não informou a fonte de energia.");
        }

        items.Add(new("Fonte de energia", status.AcLineStatus switch
        {
            0 => "Bateria",
            1 => "Tomada",
            _ => Unavailable
        }));

        if (status.BatteryFlag == byte.MaxValue)
        {
            items.Add(new("Bateria", Unavailable));
        }
        else if ((status.BatteryFlag & 128) != 0)
        {
            items.Add(new("Bateria", "Nenhuma bateria detectada"));
        }
        else
        {
            items.Add(new("Bateria", (status.BatteryFlag & 8) != 0 ? "Carregando"
                : status.AcLineStatus == 0 ? "Em uso" : "Conectada"));
            items.Add(new("Carga restante", status.BatteryLifePercent <= 100 ? $"{status.BatteryLifePercent}%" : Unavailable));
            items.Add(new("Economia de energia", status.SystemStatusFlag == 1 ? "Ativada" : "Desativada"));
            if (status.BatteryLifeTime < uint.MaxValue)
            {
                TimeSpan remaining = TimeSpan.FromSeconds(status.BatteryLifeTime);
                items.Add(new("Autonomia estimada", $"{(int)remaining.TotalHours} h {remaining.Minutes} min"));
            }

            AddBatteryDetails(items);
        }

        return new("Energia", items);
    }

    private static void AddBatteryDetails(List<DiagnosticItem> items)
    {
        var batteries = new List<(string InstanceName, uint? DesignCapacity, bool Relative)>();
        WmiDiagnosticQuery.Read(
            "SELECT InstanceName, DesignedCapacity, Capabilities FROM BatteryStaticData WHERE Active = TRUE", row =>
            {
                if (row["InstanceName"] is string name)
                {
                    batteries.Add((name, ReadCapacity(row["DesignedCapacity"]),
                        row["Capabilities"] is not uint capabilities || (capabilities & CapacityRelative) != 0));
                }
            }, BatteryNamespace);

        if (batteries.Count == 0)
        {
            int battery = 0;
            WmiDiagnosticQuery.Read("SELECT DesignCapacity, FullChargeCapacity FROM Win32_Battery", row =>
            {
                ++battery;
                AddBatteryCapacity(items, $"Bateria {battery}", ReadCapacity(row["DesignCapacity"]),
                    ReadCapacity(row["FullChargeCapacity"]), relative: false);
            });
            return;
        }

        var capacities = ReadBatteryCounter("BatteryFullChargedCapacity", "FullChargedCapacity");
        var cycles = ReadBatteryCounter("BatteryCycleCount", "CycleCount");
        var voltages = ReadBatteryCounter("BatteryStatus", "Voltage");
        for (int index = 0; index < batteries.Count; index++)
        {
            var battery = batteries[index];
            string label = $"Bateria {index + 1}";
            AddBatteryCapacity(items, label, battery.DesignCapacity, ReadCapacity(capacities.GetValueOrDefault(battery.InstanceName)),
                battery.Relative);
            if (cycles.TryGetValue(battery.InstanceName, out uint count) && count is > 0 and < uint.MaxValue)
            {
                items.Add(new($"{label} · ciclos de carga", $"{count}"));
            }

            if (ReadCapacity(voltages.GetValueOrDefault(battery.InstanceName)) is { } voltage)
            {
                items.Add(new($"{label} · tensão", $"{voltage / 1000d:0.##} V"));
            }
        }
    }

    private static Dictionary<string, uint> ReadBatteryCounter(string className, string property)
    {
        var values = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        WmiDiagnosticQuery.Read($"SELECT InstanceName, {property} FROM {className} WHERE Active = TRUE", row =>
        {
            if (row["InstanceName"] is string name && row[property] is uint number)
            {
                values.TryAdd(name, number);
            }
        }, BatteryNamespace);
        return values;
    }

    private static uint? ReadCapacity(object? value) => value is uint number and > 0 and < uint.MaxValue ? number : null;

    private static void AddBatteryCapacity(List<DiagnosticItem> items, string battery, uint? design, uint? full, bool relative)
    {
        if (!relative && design is { } designed)
        {
            items.Add(new($"{battery} · capacidade de projeto", $"{designed / 1000d:0.##} Wh"));
        }

        if (!relative && full is { } charged)
        {
            items.Add(new($"{battery} · capacidade máxima atual", $"{charged / 1000d:0.##} Wh"));
        }

        if (design > 0 && full is { } fullValue)
        {
            double health = (double)fullValue / design.Value;
            items.Add(new($"{battery} · capacidade atual sobre a de projeto", $"{health:P0}"));
        }
    }

    private static string? GetPowerPlanName()
    {
        if (PowerGetActiveScheme(0, out nint scheme) != 0 || scheme == 0)
        {
            return null;
        }

        try
        {
            uint length = 0;
            if (PowerReadFriendlyName(0, scheme, 0, 0, null, ref length) is not (0 or ErrorMoreData) ||
                length is < 2 or > 16384)
            {
                return null;
            }

            var buffer = new byte[length];
            if (PowerReadFriendlyName(0, scheme, 0, 0, buffer, ref length) != 0)
            {
                return null;
            }

            string name = Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim();
            return name.Length == 0 ? null : name;
        }
        finally
        {
            LocalFree(scheme);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint LocalFree(nint memory);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerGetActiveScheme(nint root, out nint scheme);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadFriendlyName(nint root, nint scheme, nint subgroup, nint setting, [Out] byte[]? buffer,
        ref uint bufferSize);
}
