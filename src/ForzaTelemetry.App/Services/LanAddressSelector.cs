using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace ForzaTelemetry.App.Services;

/// <summary>
/// The pure filtering and ranking rules that turn "every adapter on this PC" into "the addresses worth typing into
/// the game". No system calls, so it is unit-tested with fake adapters.
/// </summary>
public static partial class LanAddressSelector
{
    // Names/descriptions that mark VPN-like adapters: never where a console is. Word boundaries keep "TAP" from
    // matching inside unrelated words; "TAP-Windows Adapter" still matches because "-" is not a word character.
    [GeneratedRegex(@"\b(WireGuard|OpenVPN|VPN|TAP|TUN|Wintun|Tunnel)\b|Loopback", RegexOptions.IgnoreCase)]
    private static partial Regex NonLanName();

    // Virtual switches. Usually host-only noise (WSL, Hyper-V "Default Switch", VMnet), BUT a Hyper-V EXTERNAL switch
    // takes over the physical NIC and then holds the PC's only real LAN address. What tells them apart is the default
    // gateway: an external switch has one, a host-only one does not. So these are kept only with a gateway, and are
    // ranked below physical adapters.
    [GeneratedRegex(@"vEthernet|Hyper-V|VMware|VirtualBox", RegexOptions.IgnoreCase)]
    private static partial Regex VirtualName();

    /// <summary>
    /// Keeps adapters that are up, are not loopback/tunnel/PPP, are not named like a VPN, are not a virtual switch without a gateway, and have a
    /// real IPv4 (not the 169.254.x.x "no DHCP answer" fallback). Adapters with a default gateway rank first (they are
    /// the ones connected to a router, and so to the console); if at least one has a gateway, the others are dropped.
    /// </summary>
    public static IReadOnlyList<LanAddress> Select(IEnumerable<NetworkAdapterInfo> adapters)
    {
        var candidates = adapters
            .Where(a => a.IsUp && IsPhysicalLanType(a.Type) && !NonLanName().IsMatch(a.Name) && !NonLanName().IsMatch(a.Description))
            .Where(a => !IsVirtual(a) || a.HasGateway)
            .Select(a => (Adapter: a, Addresses: a.IPv4Addresses.Where(IsUsableIPv4).ToList()))
            .Where(c => c.Addresses.Count > 0)
            .ToList();

        // A gateway means "this adapter reaches a router". If some do, an adapter without one (an isolated
        // ad-hoc or host-only network) cannot be where the console is, so listing it would only add noise.
        if (candidates.Any(c => c.Adapter.HasGateway))
        {
            candidates = candidates.Where(c => c.Adapter.HasGateway).ToList();
        }

        return candidates
            .OrderBy(c => IsVirtual(c.Adapter) ? 1 : 0)
            .ThenBy(c => TypeRank(c.Adapter.Type))
            .SelectMany(c => c.Addresses.Select(address => new LanAddress(c.Adapter.Name, address)))
            .ToList();
    }

    private static bool IsVirtual(NetworkAdapterInfo a) => VirtualName().IsMatch(a.Name) || VirtualName().IsMatch(a.Description);

    private static bool IsPhysicalLanType(NetworkInterfaceType type) =>
        type is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
            or NetworkInterfaceType.Slip or NetworkInterfaceType.Unknown);

    // Wired first, then Wi-Fi, then anything else that passed the filters.
    private static int TypeRank(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => 0,
        NetworkInterfaceType.Wireless80211 => 1,
        _ => 2
    };

    private static bool IsUsableIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return !(bytes[0] == 169 && bytes[1] == 254) && !address.Equals(IPAddress.Any);
    }
}
