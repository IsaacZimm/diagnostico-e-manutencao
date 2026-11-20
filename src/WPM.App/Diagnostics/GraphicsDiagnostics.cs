using System.Globalization;
using System.Runtime.InteropServices;

namespace WPM.App.Diagnostics;

internal static class GraphicsDiagnostics
{
    private const string Unavailable = "Indisponível";
    private const int AdapterNotFound = unchecked((int)0x887A0002);
    private const uint SoftwareAdapter = 2;
    private const uint GetAdapterName = 4;

    // These slots include IUnknown and the inherited DXGI interfaces.
    private const int EnumAdapters1Slot = 12;
    private const int GetDesc1Slot = 10;
    private static readonly Guid Factory1Id = new("770aae78-f26f-4dba-a829-253c83d1b387");

    public static IReadOnlyList<DiagnosticSection> Collect()
    {
        var adapters = ReadDxgiAdapters();
        var controllers = new List<VideoController>();
        WmiDiagnosticQuery.Read("SELECT Name, PNPDeviceID, AdapterCompatibility, DriverVersion, DriverDate, InfFilename FROM Win32_VideoController",
            row => controllers.Add(new VideoController(
                ReadText(row["Name"]), ReadText(row["PNPDeviceID"]), ReadText(row["AdapterCompatibility"]),
                ReadText(row["DriverVersion"]), ReadText(row["DriverDate"]), ReadText(row["InfFilename"]))));

        var packages = new List<DriverPackage>();
        WmiDiagnosticQuery.Read("SELECT DeviceID, DriverProviderName, DriverVersion, DriverDate, InfName FROM Win32_PnPSignedDriver WHERE DeviceClass = 'DISPLAY'",
            row =>
            {
                if (ReadText(row["DeviceID"]) is { } id)
                {
                    packages.Add(new DriverPackage(id, ReadText(row["DriverProviderName"]),
                        ReadText(row["DriverVersion"]), ReadText(row["DriverDate"]), ReadText(row["InfName"])));
                }
            });

        var uniqueControllers = controllers.Distinct().ToArray();
        var sections = new List<DiagnosticSection>();

        foreach (var adapter in adapters)
        {
            var matches = uniqueControllers.Where(controller => CanMatch(adapter, controller)).ToArray();
            // A shared model or PCI ID must never copy one adapter's driver data to another.
            var controller = matches.Length == 1 && adapters.Count(candidate => CanMatch(candidate, matches[0])) == 1
                ? matches[0]
                : null;
            var package = FindDriverPackage(packages, adapter.InstanceId ?? controller?.InstanceId, controller?.DriverVersion);
            sections.Add(BuildSection(sections.Count + 1, adapter, controller, package));
        }

        foreach (var controller in uniqueControllers.Where(controller => !adapters.Any(adapter => CanMatch(adapter, controller))))
        {
            var package = FindDriverPackage(packages, controller.InstanceId, controller.DriverVersion);
            sections.Add(BuildSection(sections.Count + 1, null, controller, package));
        }

        if (adapters.Any(adapter => adapter.Vendor == 0x10DE) || uniqueControllers.Any(controller =>
            controller.InstanceId?.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) == true))
        {
            sections.AddRange(NvidiaDiagnostics.Collect());
        }

        return sections.Count > 0 ? sections : [new("Placa de vídeo", [], "O Windows não identificou um adaptador gráfico.")];
    }

    private static DiagnosticSection BuildSection(int index, DxgiAdapter? adapter, VideoController? controller, DriverPackage? package)
    {
        string? manufacturer = controller?.Manufacturer ?? GetManufacturer(adapter?.Vendor);
        var items = new List<DiagnosticItem>
        {
            new("Modelo", controller?.Name ?? adapter?.Name ?? Unavailable),
            new("VRAM dedicada (DXGI)", adapter is null ? Unavailable : FormatMemory(adapter.DedicatedMemory)),
            new("Versão do driver", package?.Version ?? controller?.DriverVersion ?? Unavailable)
        };

        if (manufacturer is not null)
        {
            items.Add(new("Fabricante", manufacturer));
        }

        if (package?.Provider is { } provider)
        {
            items.Add(new("Fornecedor do driver", provider));
        }

        if ((ReadDriverDate(package?.Date) ?? ReadDriverDate(controller?.DriverDate)) is { } date)
        {
            items.Add(new("Data do driver", date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)));
        }

        if ((package?.InfName ?? controller?.InfName) is { } infName)
        {
            items.Add(new("Pacote INF", infName));
        }

        if (adapter is { SharedMemory: > 0 })
        {
            items.Add(new("Limite de RAM compartilhada", FormatMemory(adapter.SharedMemory)));
        }

        if (adapter is { Vendor: > 0, Device: > 0 })
        {
            items.Add(new("Identificador PCI", $"{adapter.Vendor:X4}:{adapter.Device:X4} · subsistema {adapter.Subsystem:X8}"));
        }

        if (adapter?.Direct3D is { } capabilities)
        {
            if (capabilities.FeatureLevel is { } featureLevel)
            {
                items.Add(new("Nível de recurso Direct3D", featureLevel));
            }

            if (capabilities.RayTracing is { } dxr)
            {
                items.Add(new("Ray tracing (DXR)", dxr));
            }
        }

        string? issue = items.Any(item => item.Value == Unavailable) ? "A consulta não retornou todos os dados de vídeo." : null;
        return new($"Adaptador de vídeo {index}", items, issue);
    }

    private static List<DxgiAdapter> ReadDxgiAdapters()
    {
        var adapters = new List<DxgiAdapter>();
        try
        {
            if (CreateDXGIFactory1(in Factory1Id, out nint factory) < 0 || factory == 0)
            {
                return adapters;
            }

            try
            {
                var enumerate = GetComMethod<EnumAdapters1>(factory, EnumAdapters1Slot);
                for (uint index = 0; ; index++)
                {
                    int result = enumerate(factory, index, out nint adapter);
                    if (result == AdapterNotFound || result < 0 || adapter == 0)
                    {
                        break;
                    }

                    try
                    {
                        var getDescription = GetComMethod<GetDesc1>(adapter, GetDesc1Slot);
                        if (getDescription(adapter, out var desc) >= 0 && (desc.Flags & SoftwareAdapter) == 0)
                        {
                            adapters.Add(new DxgiAdapter(ReadText(desc.Description), ReadInstanceId(desc.AdapterId),
                                desc.VendorId, desc.DeviceId, desc.SubsystemId, desc.Revision,
                                (ulong)desc.DedicatedVideoMemory, (ulong)desc.SharedSystemMemory, Direct3DCapabilities.Read(adapter)));
                        }
                    }
                    finally
                    {
                        Marshal.Release(adapter);
                    }
                }
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            // Keep WMI as the adapter inventory when DXGI is unavailable.
        }

        return adapters;
    }

    private static T GetComMethod<T>(nint instance, int slot) where T : Delegate
    {
        nint vtable = Marshal.ReadIntPtr(instance);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
    }

    private static string? ReadInstanceId(Luid adapterId)
    {
        var request = new DisplayConfigAdapterName
        {
            Type = GetAdapterName,
            Size = (uint)Marshal.SizeOf<DisplayConfigAdapterName>(),
            AdapterId = adapterId,
            DevicePath = ""
        };

        if (DisplayConfigGetDeviceInfo(ref request) != 0 || !request.DevicePath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return null;
        }

        string[] parts = request.DevicePath[4..].Split('#');
        return parts is [var enumerator, var device, var instance, var interfaceClass]
            && enumerator.Length > 0 && device.Length > 0 && instance.Length > 0 && Guid.TryParse(interfaceClass, out _)
            ? $"{enumerator}\\{device}\\{instance}"
            : null;
    }

    private static bool CanMatch(DxgiAdapter adapter, VideoController controller)
    {
        if (adapter.InstanceId is not null && controller.InstanceId is not null)
        {
            return adapter.InstanceId.Equals(controller.InstanceId, StringComparison.OrdinalIgnoreCase);
        }

        if (adapter.Vendor is > 0 and <= ushort.MaxValue && adapter.Device is > 0 and <= ushort.MaxValue
            && controller.InstanceId is { } id && id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase))
        {
            var hardwareIds = id.Split('\\')[1].Split('&');
            return hardwareIds.Contains($"VEN_{adapter.Vendor:X4}", StringComparer.OrdinalIgnoreCase)
                && hardwareIds.Contains($"DEV_{adapter.Device:X4}", StringComparer.OrdinalIgnoreCase)
                && MatchesOptionalId(hardwareIds, "SUBSYS_", adapter.Subsystem, "X8")
                && MatchesOptionalId(hardwareIds, "REV_", adapter.Revision, "X2");
        }

        return adapter.Name is not null && adapter.Name.Equals(controller.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesOptionalId(string[] hardwareIds, string prefix, uint dxgiValue, string format)
    {
        var hardwareId = hardwareIds.FirstOrDefault(id => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return dxgiValue == 0 || hardwareId is null || hardwareId.Equals(prefix + dxgiValue.ToString(format, CultureInfo.InvariantCulture),
            StringComparison.OrdinalIgnoreCase);
    }

    private static DriverPackage? FindDriverPackage(List<DriverPackage> packages, string? instanceId, string? currentVersion)
    {
        if (instanceId is null)
        {
            return null;
        }

        var matches = packages.Where(package => package.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
        if (matches.Length == 1 && (currentVersion is null || matches[0].Version is null || matches[0].Version == currentVersion))
        {
            return matches[0];
        }

        var matchingVersions = matches.Where(package => currentVersion is not null && package.Version == currentVersion).ToArray();
        return matchingVersions.Length == 1 ? matchingVersions[0] : null;
    }

    private static string? ReadText(object? value) => value is string text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static DateOnly? ReadDriverDate(string? value)
    {
        if (value is { Length: >= 8 } && DateOnly.TryParseExact(value.AsSpan(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            return date;
        }

        return DateOnly.TryParseExact(value, "M-d-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) ? date : null;
    }

    private static string FormatMemory(ulong bytes)
    {
        const double MiB = 1024d * 1024;
        const double GiB = 1024 * MiB;
        return bytes >= GiB ? $"{bytes / GiB:0.##} GiB" : $"{bytes / MiB:0.##} MiB";
    }

    private static string? GetManufacturer(uint? id) => id switch
    {
        0x10DE => "NVIDIA",
        0x1002 => "AMD",
        0x8086 => "Intel",
        0x5143 => "Qualcomm",
        0x1414 => "Microsoft",
        _ => null
    };

    private sealed record DxgiAdapter(string? Name, string? InstanceId, uint Vendor, uint Device, uint Subsystem, uint Revision,
        ulong DedicatedMemory, ulong SharedMemory, Direct3DCapabilities? Direct3D);
    private sealed record VideoController(string? Name, string? InstanceId, string? Manufacturer, string? DriverVersion,
        string? DriverDate, string? InfName);
    private sealed record DriverPackage(string InstanceId, string? Provider, string? Version, string? Date, string? InfName);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubsystemId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public Luid AdapterId;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigAdapterName
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1(nint factory, uint index, out nint adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1(nint adapter, out AdapterDescription description);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid interfaceId, out nint factory);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigAdapterName request);
}
