using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WPM.App.Diagnostics;

internal static class PeripheralDiagnostics
{
    private const uint DevicesPresent = 0x2;
    private const uint DevPropTypeString = 0x12;
    private const int NoMoreItems = 259;
    private const int DeviceInstanceIdCapacity = 200;
    private static readonly Guid MonitorClass = new("4D36E96E-E325-11CE-BFC1-08002BE10318");
    private static readonly Guid KeyboardClass = new("4D36E96B-E325-11CE-BFC1-08002BE10318");
    private static readonly Guid MouseClass = new("4D36E96F-E325-11CE-BFC1-08002BE10318");
    private static readonly DevicePropertyKey DeviceName = new(new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), 10);
    private static readonly DevicePropertyKey BusReportedDescription = new(new Guid("540B947E-8B40-45BC-A8A2-6A0B894CBDA2"), 4);

    public static IReadOnlyList<DiagnosticSection> Collect() =>
    [
        CollectMonitors(),
        CollectInputDevices(KeyboardClass, "Teclados", "Função de teclado"),
        CollectInputDevices(MouseClass, "Mouses", "Função de mouse")
    ];

    private static DiagnosticSection CollectMonitors()
    {
        var (present, pnpIssue) = ReadPresentDevices(MonitorClass);
        var active = new List<MonitorReading>();
        string? edidIssue = WmiDiagnosticQuery.Read(
            "SELECT InstanceName, UserFriendlyName, ManufacturerName, ProductCodeID FROM WmiMonitorID WHERE Active = TRUE",
            monitor => active.Add(new MonitorReading(
                monitor["InstanceName"] as string,
                DecodeEdid(monitor["UserFriendlyName"]),
                DecodeEdid(monitor["ManufacturerName"]),
                DecodeEdid(monitor["ProductCodeID"]))),
            @"root\wmi");

        var items = new List<DiagnosticItem>();
        if (active.Count == 0)
        {
            for (int index = 0; index < present.Count; index++)
            {
                DeviceNode device = present[index];
                string? name = ReadProperty(device.DevInst, DeviceName) ?? ReadProperty(device.DevInst, BusReportedDescription);
                items.Add(new($"Monitor {index + 1} · nome no Windows", name ?? "Monitor sem modelo informado"));
            }

            return new("Monitores", items, items.Count == 0
                ? pnpIssue ?? edidIssue ?? "Nenhum monitor foi identificado pelo Windows."
                : "O Windows informou os monitores presentes pelo Plug and Play. Os dados do monitor ativo ficaram indisponíveis.");
        }

        var outputTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        WmiDiagnosticQuery.Read(
            "SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams WHERE Active = TRUE",
            monitor =>
            {
                if (monitor["InstanceName"] is string instance && MonitorConnection(monitor["VideoOutputTechnology"]) is { } connection)
                {
                    outputTypes.TryAdd(instance, connection);
                }
            }, @"root\wmi");

        bool missingModel = false;
        for (int index = 0; index < active.Count; index++)
        {
            var entry = active[index];
            string label = $"Monitor {index + 1}";
            string? displayId = EdidDeviceId(entry.InstanceName);
            var pnp = displayId is null ? null : present.FirstOrDefault(device =>
                string.Equals(device.InstanceId, displayId, StringComparison.OrdinalIgnoreCase));
            string? windowsName = pnp is null ? null : ReadProperty(pnp.DevInst, DeviceName);
            string? model = entry.Model;
            if (model is not null && entry.Manufacturer is { } manufacturer
                && !model.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase))
            {
                model = $"{manufacturer} {model}";
            }

            model ??= windowsName;
            items.Add(new($"{label} · modelo", model ?? "Monitor ativo sem modelo informado"));
            missingModel |= model is null;

            if (windowsName is not null && !string.Equals(windowsName, model, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new($"{label} · nome no Windows", windowsName));
            }

            if (entry.Manufacturer is not null)
            {
                items.Add(new($"{label} · fabricante informado", entry.Manufacturer));
            }

            if (entry.ProductCode is not null)
            {
                items.Add(new($"{label} · código do modelo", entry.ProductCode));
            }

            if (entry.InstanceName is { } instance && outputTypes.TryGetValue(instance, out string? transport))
            {
                items.Add(new($"{label} · conexão", transport));
            }
        }

        return new("Monitores", items, edidIssue ?? (missingModel ? "Nem todo monitor ativo informou um modelo." : null));
    }

    private static DiagnosticSection CollectInputDevices(Guid deviceClass, string title, string label)
    {
        var (devices, issue) = ReadPresentDevices(deviceClass);
        var peripherals = devices.Select(ReadInputDevice).GroupBy(device => device.PhysicalDevInst);
        var items = new List<DiagnosticItem>();
        foreach (var group in peripherals)
        {
            InputReading device = group.First();
            string description = device.Name;
            if (device.Transport is not null)
            {
                description += $" · {device.Transport}";
            }

            int interfaceCount = group.Count();
            if (interfaceCount > 1)
            {
                description += $" · {interfaceCount} interfaces";
            }

            items.Add(new($"{label} {items.Count + 1}", description));
        }

        return new(title, items, issue ?? (items.Count == 0 ? "Nenhum dispositivo presente foi identificado nesta categoria." : null));
    }

    private static InputReading ReadInputDevice(DeviceNode device)
    {
        uint current = device.DevInst;
        string? instanceId = device.InstanceId;
        string? busName = ReadProperty(current, BusReportedDescription);
        for (int level = 0; level < 6 && instanceId is not null; level++)
        {
            string? transport = DeviceTransport(instanceId);
            if (transport is not null)
            {
                string? name = ReadProperty(current, BusReportedDescription) ?? busName ?? ReadProperty(device.DevInst, DeviceName);
                return new InputReading(current, name ?? "Dispositivo sem modelo informado", transport);
            }

            if (CM_Get_Parent(out uint parent, current, 0) != 0 || parent == current)
            {
                break;
            }

            current = parent;
            instanceId = ReadInstanceId(current);
            if (instanceId is not null && instanceId.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase))
            {
                busName ??= ReadProperty(current, BusReportedDescription);
            }
        }

        return new InputReading(device.DevInst, busName ?? ReadProperty(device.DevInst, DeviceName)
            ?? "Dispositivo sem modelo informado", null);
    }

    private static (List<DeviceNode> Devices, string? Issue) ReadPresentDevices(Guid deviceClass)
    {
        var devices = new List<DeviceNode>();
        using var set = SetupDiGetClassDevs(ref deviceClass, null, IntPtr.Zero, DevicesPresent);
        if (set.IsInvalid)
        {
            return (devices, "O Windows não disponibilizou a lista de dispositivos presentes.");
        }

        var info = new DeviceInfoData { Size = (uint)Marshal.SizeOf<DeviceInfoData>() };
        for (uint index = 0; ; index++)
        {
            if (!SetupDiEnumDeviceInfo(set, index, ref info))
            {
                return (devices, Marshal.GetLastPInvokeError() == NoMoreItems ? null : "A lista de dispositivos presentes ficou incompleta.");
            }

            devices.Add(new DeviceNode(info.DevInst, ReadInstanceId(info.DevInst)));
        }
    }

    private static string? ReadInstanceId(uint deviceInstance)
    {
        var value = new StringBuilder(DeviceInstanceIdCapacity);
        return CM_Get_Device_ID(deviceInstance, value, (uint)value.Capacity, 0) == 0 ? value.ToString() : null;
    }

    private static string? ReadProperty(uint deviceInstance, DevicePropertyKey key)
    {
        var bytes = new byte[1024];
        uint count = (uint)bytes.Length;
        return CM_Get_DevNode_Property(deviceInstance, ref key, out uint type, bytes, ref count, 0) == 0
            && type == DevPropTypeString && count <= bytes.Length && count % 2 == 0
            ? CleanText(Encoding.Unicode.GetString(bytes, 0, (int)count))
            : null;
    }

    private static string? CleanText(string text)
    {
        text = text.Split('\0', 2)[0].Trim();
        return text.Length == 0 ? null : new string(text.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
    }

    private static string? DecodeEdid(object? value) => value is ushort[] characters
        ? CleanText(new string(characters.Select(code => (char)code).ToArray())) : null;

    private static string? EdidDeviceId(string? instanceName)
    {
        if (instanceName is null)
        {
            return null;
        }

        int suffix = instanceName.LastIndexOf('_');
        return suffix >= 0 && suffix + 1 < instanceName.Length && instanceName[(suffix + 1)..].All(char.IsAsciiDigit)
            ? instanceName[..suffix] : instanceName;
    }

    private static string? DeviceTransport(string instanceId)
    {
        if (instanceId.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase)
            && !instanceId.Split('\\', 3)[1].Contains("&MI_", StringComparison.OrdinalIgnoreCase))
        {
            return "USB";
        }

        return instanceId.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase)
            || instanceId.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase) ? "Bluetooth" : null;
    }

    private static string? MonitorConnection(object? value) => value switch
    {
        0u => "VGA",
        4u => "DVI",
        5u => "HDMI",
        6u => "LVDS",
        10u => "DisplayPort",
        11u => "DisplayPort interno",
        15u => "Miracast",
        16u => "Conexão indireta com fio",
        17u => "Conexão virtual",
        0x80000000u => "Tela interna",
        _ => null
    };

    private sealed record DeviceNode(uint DevInst, string? InstanceId);
    private sealed record InputReading(uint PhysicalDevInst, string Name, string? Transport);
    private sealed record MonitorReading(string? InstanceName, string? Model, string? Manufacturer, string? ProductCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DevicePropertyKey(Guid formatId, uint propertyId)
    {
        private readonly Guid format = formatId;
        private readonly uint property = propertyId;
    }

    private sealed class DeviceInfoSetHandle : SafeHandleMinusOneIsInvalid
    {
        private DeviceInfoSetHandle() : base(true) { }

        protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle);
    }

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern DeviceInfoSetHandle SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, nint parentWindow, uint flags);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(DeviceInfoSetHandle devices, uint index, ref DeviceInfoData data);

    [DllImport("setupapi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint devices);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Parent(out uint parent, uint device, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_ID(uint device, StringBuilder instanceId, uint charCount, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW", ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_Property(uint device, ref DevicePropertyKey key, out uint propertyType,
        [Out] byte[] buffer, ref uint bufferLength, uint flags);
}
