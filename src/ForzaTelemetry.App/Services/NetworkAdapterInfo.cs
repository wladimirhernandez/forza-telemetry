using System.Net;
using System.Net.NetworkInformation;

namespace ForzaTelemetry.App.Services;

/// <summary>
/// Plain snapshot of one network adapter, holding only what the LAN-address filter needs. Keeping it a simple record
/// (instead of passing <see cref="NetworkInterface"/>) lets tests build fake adapters; the real class cannot be created.
/// </summary>
public sealed record NetworkAdapterInfo(
    string Name,
    string Description,
    NetworkInterfaceType Type,
    bool IsUp,
    bool HasGateway,
    IReadOnlyList<IPAddress> IPv4Addresses);

/// <summary>One address the user can type into the game's Data Out IP field, with the adapter it belongs to.</summary>
public sealed record LanAddress(string AdapterName, IPAddress Address);
