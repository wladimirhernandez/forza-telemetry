using System.Net;
using System.Net.NetworkInformation;
using ForzaTelemetry.App.Services;

namespace ForzaTelemetry.App.Tests;

public class LanAddressSelectorTests
{
    private static NetworkAdapterInfo Adapter(
        string name,
        string ip,
        NetworkInterfaceType type = NetworkInterfaceType.Ethernet,
        bool up = true,
        bool gateway = true,
        string? description = null) =>
        new(name, description ?? name, type, up, gateway, [IPAddress.Parse(ip)]);

    private static string[] Ips(IEnumerable<NetworkAdapterInfo> adapters) =>
        LanAddressSelector.Select(adapters).Select(a => a.Address.ToString()).ToArray();

    [Fact]
    public void Keeps_a_Wi_Fi_adapter_that_is_up_with_a_gateway()
    {
        Assert.Equal(["192.168.0.204"], Ips([Adapter("Wi-Fi", "192.168.0.204", NetworkInterfaceType.Wireless80211)]));
    }

    [Fact]
    public void Drops_adapters_that_are_down()
    {
        Assert.Empty(Ips([Adapter("Ethernet", "192.168.1.5", up: false)]));
    }

    [Theory]
    [InlineData(NetworkInterfaceType.Loopback)]
    [InlineData(NetworkInterfaceType.Tunnel)]
    [InlineData(NetworkInterfaceType.Ppp)]
    public void Drops_loopback_tunnel_and_ppp_types(NetworkInterfaceType type)
    {
        Assert.Empty(Ips([Adapter("Something", "10.8.0.2", type)]));
    }

    [Theory]
    [InlineData("Surfshark WireGuard", "Surfshark WireGuard Tunnel")]
    [InlineData("Ethernet 2", "TAP-Windows Adapter V9")]
    [InlineData("Proton VPN", "ProtonVPN TUN")]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter")]
    [InlineData("VMware Network Adapter VMnet1", "VMware Virtual Ethernet Adapter")]
    public void Drops_vpn_like_and_virtual_adapters_even_when_typed_as_Ethernet(string name, string description)
    {
        Assert.Empty(Ips([Adapter(name, "10.8.0.2", NetworkInterfaceType.Ethernet, description: description)]));
    }

    [Fact]
    public void Does_not_mistake_ordinary_words_for_vpn_markers()
    {
        // "Intel(R) Ethernet Connection (7) I219-V" contains "V" and "Ethernet" but no VPN/TAP/TUN word.
        var adapter = Adapter("Ethernet", "192.168.1.5", description: "Intel(R) Ethernet Connection (7) I219-V");
        Assert.Equal(["192.168.1.5"], Ips([adapter]));
    }

    [Fact]
    public void Drops_the_169_254_fallback_address()
    {
        Assert.Empty(Ips([Adapter("Ethernet", "169.254.12.7")]));
    }

    [Fact]
    public void Prefers_adapters_with_a_gateway_and_drops_those_without_when_any_has_one()
    {
        var withGateway = Adapter("Wi-Fi", "192.168.0.204", NetworkInterfaceType.Wireless80211);
        var isolated = Adapter("Ethernet 3", "192.168.56.1", gateway: false);

        Assert.Equal(["192.168.0.204"], Ips([isolated, withGateway]));
    }

    [Fact]
    public void Keeps_adapters_without_a_gateway_when_none_has_one()
    {
        Assert.Equal(["192.168.56.1"], Ips([Adapter("Ethernet 3", "192.168.56.1", gateway: false)]));
    }

    [Fact]
    public void Lists_wired_before_Wi_Fi()
    {
        var wifi = Adapter("Wi-Fi", "192.168.0.204", NetworkInterfaceType.Wireless80211);
        var wired = Adapter("Ethernet", "192.168.0.10");

        Assert.Equal(["192.168.0.10", "192.168.0.204"], Ips([wifi, wired]));
    }

    [Fact]
    public void Ignores_IPv6_addresses_and_keeps_every_IPv4_of_an_adapter()
    {
        var adapter = new NetworkAdapterInfo(
            "Wi-Fi", "Wi-Fi", NetworkInterfaceType.Wireless80211, true, true,
            [IPAddress.Parse("fe80::1"), IPAddress.Parse("192.168.0.204"), IPAddress.Parse("192.168.0.205")]);

        Assert.Equal(["192.168.0.204", "192.168.0.205"], Ips([adapter]));
    }

    [Fact]
    public void Returns_empty_for_no_adapters()
    {
        Assert.Empty(Ips([]));
    }
}
