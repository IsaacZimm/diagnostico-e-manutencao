using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WPM.App.Diagnostics;

internal static class NetworkDiagnostics
{
    private const string Unavailable = "Indisponível";
    private const int NormalWithoutStatistics = 2;
    private const uint AdministrativelyDisabled = 2;
    private const byte HardwareInterface = 0x01;
    private const byte FilterOrEndpointInterface = 0x82;

    public static IReadOnlyList<DiagnosticSection> Collect()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return [new("Rede", [], "O Windows não disponibilizou a lista de adaptadores de rede.")];
        }

        var sections = new List<AdapterSection>();
        foreach (NetworkInterface adapter in interfaces)
        {
            if (DescribeAdapter(adapter) is { } section)
            {
                sections.Add(section);
            }
        }

        return sections.Count == 0
            ? [new("Rede", [], "Nenhum adaptador de rede utilizável foi identificado.")]
            : sections.OrderByDescending(section => section.IsUp)
                .ThenByDescending(section => section.HasGateway)
                .ThenByDescending(section => section.IsPhysical)
                .ThenBy(section => section.Section.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(section => section.Section).ToArray();
    }

    private static AdapterSection? DescribeAdapter(NetworkInterface adapter)
    {
        var type = adapter.NetworkInterfaceType;
        if (type == NetworkInterfaceType.Loopback || adapter.OperationalStatus == OperationalStatus.NotPresent)
        {
            return null;
        }

        AdapterState? native = ReadNativeState(adapter.Id);
        if (native is { } state && ((state.Flags & FilterOrEndpointInterface) != 0 || IsAutomaticTunnel(state.TunnelType)))
        {
            return null;
        }

        bool? physical = native is { } metadata ? (metadata.Flags & HardwareInterface) != 0 : null;
        bool isUp = adapter.OperationalStatus == OperationalStatus.Up;
        bool isTunnel = type is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel;
        bool hasIpBinding = adapter.Supports(NetworkInterfaceComponent.IPv4) || adapter.Supports(NetworkInterfaceComponent.IPv6);
        if (!hasIpBinding && !(physical == true && IsEthernetOrWifi(type)))
        {
            return null;
        }

        var configuration = ReadIpConfiguration(adapter);
        bool hasGateway = configuration?.Gateways.Length > 0;
        bool hasUsableAddress = configuration?.Addresses.Any(address => !IsLinkLocal(address.Address)) == true;

        // Hidden transition interfaces and inactive virtual adapters are not useful as connections.
        if (physical == false && (!isUp || !(hasGateway || hasUsableAddress)))
        {
            return null;
        }

        if (physical is null && (!isUp || !(hasGateway || hasUsableAddress)) && !IsEthernetOrWifi(type))
        {
            return null;
        }

        string medium = DescribeMedium(type);
        string classification = physical switch
        {
            true => $"{medium} · físico",
            false when isTunnel => "VPN / túnel · virtual",
            false => $"{medium} · virtual",
            _ => medium
        };
        var items = new List<DiagnosticItem>
        {
            new("Adaptador", DisplayOrUnavailable(adapter.Description)),
            new("Tipo", classification),
            new("Estado", DescribeStatus(adapter.OperationalStatus, native?.AdminStatus == AdministrativelyDisabled))
        };

        AppendSpeed(items, adapter, native, isUp, physical == false || isTunnel);
        if (configuration is null)
        {
            items.Add(new("Configuração IP", Unavailable));
        }
        else if (configuration.Addresses.Length == 0)
        {
            items.Add(new("Endereço IP", "Sem endereço atribuído"));
        }
        else
        {
            AppendAddresses(items, configuration);
        }

        string title = string.IsNullOrWhiteSpace(adapter.Name) ? "Rede" : $"Rede · {adapter.Name.Trim()}";
        string? issue = items.Any(item => item.Value == Unavailable)
            ? "Alguns dados deste adaptador não foram disponibilizados pelo Windows."
            : null;
        return new(new DiagnosticSection(title, items, issue), isUp, hasGateway, physical == true);
    }

    private static IpConfiguration? ReadIpConfiguration(NetworkInterface adapter)
    {
        try
        {
            var properties = adapter.GetIPProperties();
            var addresses = properties.UnicastAddresses
                .Where(address => IsUsableIp(address.Address) && address.DuplicateAddressDetectionState is not
                    (DuplicateAddressDetectionState.Invalid or DuplicateAddressDetectionState.Tentative or DuplicateAddressDetectionState.Duplicate))
                .Select(address => new NetworkAddress(address.Address, address.PrefixLength)).Distinct().ToArray();
            var gateways = properties.GatewayAddresses.Select(gateway => gateway.Address).Where(IsUsableIp).Distinct().ToArray();
            return new(addresses, gateways);
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static void AppendAddresses(List<DiagnosticItem> items, IpConfiguration configuration)
    {
        var ipv4 = configuration.Addresses.Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork).ToArray();
        for (int index = 0; index < ipv4.Length; index++)
        {
            string suffix = ipv4.Length == 1 ? "" : $" {index + 1}";
            items.Add(new($"IPv4{suffix}", ipv4[index].Address.ToString()));
            items.Add(new($"Máscara de sub-rede{suffix}", FormatIpv4Mask(ipv4[index].PrefixLength)));
        }

        var ipv6 = configuration.Addresses.Where(address => address.Address.AddressFamily == AddressFamily.InterNetworkV6)
            .OrderBy(address => address.Address.IsIPv6LinkLocal).ToArray();
        foreach (NetworkAddress address in ipv6)
        {
            string label = address.Address.IsIPv6LinkLocal ? "IPv6 local (link)" : "IPv6";
            string value = address.PrefixLength is >= 0 and <= 128 ? $"{address.Address}/{address.PrefixLength}" : address.Address.ToString();
            items.Add(new(label, value));
        }

        foreach (var group in configuration.Gateways.GroupBy(address => address.AddressFamily).OrderBy(group => group.Key))
        {
            string family = group.Key == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
            items.Add(new($"Gateway padrão {family}", string.Join(", ", group)));
        }
    }

    private static void AppendSpeed(List<DiagnosticItem> items, NetworkInterface adapter, AdapterState? native, bool isUp, bool isVirtual)
    {
        if (isVirtual)
        {
            items.Add(new("Velocidade do link", "Interface virtual"));
            return;
        }

        if (!isUp)
        {
            items.Add(new("Velocidade do link", "Sem link ativo"));
            return;
        }

        ulong rx = native?.ReceiveBitsPerSecond ?? 0;
        ulong tx = native?.TransmitBitsPerSecond ?? 0;
        if (HasSpeed(rx) && rx == tx)
        {
            items.Add(new("Velocidade do link (Rx/Tx)", FormatSpeed(rx)));
            return;
        }

        if (!HasSpeed(tx))
        {
            try
            {
                long speed = adapter.Speed;
                tx = speed > 0 ? (ulong)speed : 0;
            }
            catch (NetworkInformationException)
            {
                tx = 0;
            }
        }

        if (HasSpeed(rx) || HasSpeed(tx))
        {
            items.Add(new("Link · recepção", FormatSpeed(rx)));
            items.Add(new("Link · transmissão", FormatSpeed(tx)));
            return;
        }

        items.Add(new("Velocidade do link", Unavailable));
    }

    private static AdapterState? ReadNativeState(string id)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063)
            || !Guid.TryParse(id, out var guid) || ConvertInterfaceGuidToLuid(in guid, out ulong luid) != 0)
        {
            return null;
        }

        var row = new InterfaceRow { InterfaceLuid = luid };
        return GetIfEntry2Ex(NormalWithoutStatistics, ref row) == 0
            ? new(row.Flags, row.AdminStatus, row.TunnelType, row.ReceiveLinkSpeed, row.TransmitLinkSpeed)
            : null;
    }

    private static bool IsUsableIp(IPAddress address) => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
        && !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any);

    private static bool IsLinkLocal(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal;
        }

        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    private static string FormatIpv4Mask(int prefix)
    {
        if (prefix is < 0 or > 32)
        {
            return Unavailable;
        }

        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        return $"{mask >> 24}.{(mask >> 16) & 255}.{(mask >> 8) & 255}.{mask & 255}";
    }

    private static bool HasSpeed(ulong speed) => speed is > 0 and < ulong.MaxValue;

    private static string FormatSpeed(ulong speed) => speed switch
    {
        0 or ulong.MaxValue => Unavailable,
        >= 1_000_000_000 => $"{speed / 1_000_000_000d:0.##} Gb/s",
        >= 1_000_000 => $"{speed / 1_000_000d:0.##} Mb/s",
        >= 1_000 => $"{speed / 1_000d:0.##} kb/s",
        _ => $"{speed} bit/s"
    };

    private static bool IsAutomaticTunnel(uint type) => type is 11 or 13 or 14; // 6to4, ISATAP, Teredo.

    private static bool IsEthernetOrWifi(NetworkInterfaceType type) => type is NetworkInterfaceType.Wireless80211
        or NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit or NetworkInterfaceType.FastEthernetT
        or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.GigabitEthernet;

    private static string DescribeMedium(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel => "VPN / túnel",
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Banda larga móvel",
        _ when IsEthernetOrWifi(type) => "Ethernet",
        _ => "Interface de rede"
    };

    private static string DescribeStatus(OperationalStatus status, bool disabled) => status switch
    {
        _ when disabled => "Desativado",
        OperationalStatus.Up => "Conectado",
        OperationalStatus.Down or OperationalStatus.LowerLayerDown => "Desconectado",
        OperationalStatus.Dormant => "Aguardando conexão",
        OperationalStatus.Testing => "Em teste",
        _ => Unavailable
    };

    private static string DisplayOrUnavailable(string text) => string.IsNullOrWhiteSpace(text) ? Unavailable : text.Trim();

    private sealed record AdapterSection(DiagnosticSection Section, bool IsUp, bool HasGateway, bool IsPhysical);
    private sealed record IpConfiguration(NetworkAddress[] Addresses, IPAddress[] Gateways);
    private readonly record struct NetworkAddress(IPAddress Address, int PrefixLength);
    private readonly record struct AdapterState(byte Flags, uint AdminStatus, uint TunnelType, ulong ReceiveBitsPerSecond, ulong TransmitBitsPerSecond);

    // Native MIB_IF_ROW2, including the unused trailing counters written by IP Helper.
    [StructLayout(LayoutKind.Explicit, Size = 1352)]
    private struct InterfaceRow
    {
        [FieldOffset(0)] public ulong InterfaceLuid;
        [FieldOffset(1132)] public uint TunnelType;
        [FieldOffset(1152)] public byte Flags;
        [FieldOffset(1160)] public uint AdminStatus;
        [FieldOffset(1192)] public ulong TransmitLinkSpeed;
        [FieldOffset(1200)] public ulong ReceiveLinkSpeed;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint ConvertInterfaceGuidToLuid(in Guid interfaceGuid, out ulong interfaceLuid);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIfEntry2Ex(int level, ref InterfaceRow row);
}
