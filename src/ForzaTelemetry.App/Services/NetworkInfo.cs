using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ForzaTelemetry.App.Services;

/// <summary>Real <see cref="INetworkInfo"/>: reads the adapters from the OS and applies <see cref="LanAddressSelector"/>.</summary>
public sealed class NetworkInfo : INetworkInfo
{
    public IReadOnlyList<LanAddress> GetLanAddresses()
    {
        try
        {
            var adapters = new List<NetworkAdapterInfo>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    adapters.Add(ToInfo(nic));
                }
                catch (NetworkInformationException)
                {
                    // One adapter that cannot describe itself (a virtual NIC mid-reconfiguration) must not hide the others.
                }
            }

            return LanAddressSelector.Select(adapters);
        }
        catch (NetworkInformationException)
        {
            // Showing "no address found" is a fine degraded answer; the dashboard itself does not need this.
            return [];
        }
    }

    private static NetworkAdapterInfo ToInfo(NetworkInterface nic)
    {
        var properties = nic.GetIPProperties();
        return new NetworkAdapterInfo(
            nic.Name,
            nic.Description,
            nic.NetworkInterfaceType,
            nic.OperationalStatus == OperationalStatus.Up,
            properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                 && !g.Address.Equals(System.Net.IPAddress.Any)),
            properties.UnicastAddresses.Select(u => u.Address).ToList());
    }
}
