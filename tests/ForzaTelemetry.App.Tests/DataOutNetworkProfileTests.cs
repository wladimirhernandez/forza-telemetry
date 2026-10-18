using System.Net;
using ForzaTelemetry.App.Services;
using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.App.Tests;

/// <summary>The Public-network warning of the Data Out guide, driven by a fake network profile source.</summary>
public class DataOutNetworkProfileTests
{
    private sealed class FixedNetworkInfo(params string[] ips) : INetworkInfo
    {
        public IReadOnlyList<LanAddress> GetLanAddresses() =>
            ips.Select(ip => new LanAddress("Wi-Fi", IPAddress.Parse(ip))).ToList();
    }

    private sealed class FakeProfile(NetworkCategory category = NetworkCategory.Unknown) : INetworkProfileInfo
    {
        public NetworkCategory Category { get; set; } = category;

        public List<IPAddress> Asked { get; } = [];

        public Exception? Throw { get; set; }

        public Task<NetworkCategory> GetCategoryAsync(IPAddress localAddress, CancellationToken cancellationToken = default)
        {
            Asked.Add(localAddress);
            return Throw is null ? Task.FromResult(Category) : Task.FromException<NetworkCategory>(Throw);
        }
    }

    private sealed class DelegateProfile(Func<Task<NetworkCategory>> answer) : INetworkProfileInfo
    {
        public Task<NetworkCategory> GetCategoryAsync(IPAddress localAddress, CancellationToken cancellationToken = default) => answer();
    }

    [Fact]
    public void A_public_network_raises_the_warning_for_the_primary_address()
    {
        var profile = new FakeProfile(NetworkCategory.Public);

        var dataOut = new DataOutViewModel(5300, new FixedNetworkInfo("192.168.0.204", "10.0.0.7"), networkProfile: profile);

        Assert.True(dataOut.IsPublicNetwork);
        Assert.Equal([IPAddress.Parse("192.168.0.204")], profile.Asked); // the address the user is told to type, not every adapter
    }

    [Theory]
    [InlineData(NetworkCategory.Private)]
    [InlineData(NetworkCategory.Domain)]
    [InlineData(NetworkCategory.Unknown)]
    public void Only_a_public_network_warns(NetworkCategory category)
    {
        var dataOut = new DataOutViewModel(5300, new FixedNetworkInfo("192.168.0.204"), networkProfile: new FakeProfile(category));

        Assert.False(dataOut.IsPublicNetwork);
    }

    [Fact]
    public void Without_a_profile_source_or_without_an_address_there_is_no_warning_and_no_lookup()
    {
        var profile = new FakeProfile(NetworkCategory.Public);

        Assert.False(new DataOutViewModel(5300, new FixedNetworkInfo("192.168.0.204")).IsPublicNetwork);
        Assert.False(new DataOutViewModel(5300, new FixedNetworkInfo(), networkProfile: profile).IsPublicNetwork);
        Assert.Empty(profile.Asked);
    }

    [Fact]
    public async Task A_provider_that_throws_is_logged_and_never_warns_or_breaks()
    {
        var logger = new ListLogger<DataOutViewModel>();
        var profile = new FakeProfile { Throw = new InvalidOperationException("nlm down") };

        var dataOut = new DataOutViewModel(5300, new FixedNetworkInfo("192.168.0.204"), logger: logger, networkProfile: profile);
        await dataOut.RefreshNetworkCategoryAsync();

        Assert.False(dataOut.IsPublicNetwork);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task Check_again_picks_up_the_user_switching_the_network_to_private()
    {
        var profile = new FakeProfile(NetworkCategory.Public);
        var dataOut = new DataOutViewModel(5300, new FixedNetworkInfo("192.168.0.204"), networkProfile: profile);
        Assert.True(dataOut.IsPublicNetwork);

        profile.Category = NetworkCategory.Private; // the user changed it in Windows Settings
        await dataOut.RefreshNetworkCategoryCommand.ExecuteAsync(null);

        Assert.False(dataOut.IsPublicNetwork);
    }

    [Fact]
    public async Task A_slow_answer_for_an_old_lookup_never_overwrites_a_newer_one()
    {
        var first = new TaskCompletionSource<NetworkCategory>();
        var calls = 0;
        var provider = new DelegateProfile(() => ++calls == 1 ? first.Task : Task.FromResult(NetworkCategory.Private));
        var dataOut = new DataOutViewModel(5300, new FixedNetworkInfo("192.168.0.204"), networkProfile: provider); // lookup 1 is pending

        await dataOut.RefreshNetworkCategoryAsync(); // lookup 2 answers Private first
        first.SetResult(NetworkCategory.Public); // lookup 1 finally answers Public: stale
        await Task.Delay(30);

        Assert.False(dataOut.IsPublicNetwork);
    }

    [Fact]
    public async Task Refreshing_the_addresses_looks_the_profile_up_again()
    {
        var profile = new FakeProfile(NetworkCategory.Private);
        var dataOut = new DataOutViewModel(5300, new FixedNetworkInfo("192.168.0.204"), networkProfile: profile);

        profile.Category = NetworkCategory.Public;
        await dataOut.RefreshAsync();

        Assert.True(await Wait.UntilAsync(() => dataOut.IsPublicNetwork));
    }

    [Theory]
    [InlineData(0, NetworkCategory.Public)]
    [InlineData(1, NetworkCategory.Private)]
    [InlineData(2, NetworkCategory.Domain)]
    [InlineData(3, NetworkCategory.Unknown)]
    [InlineData(-1, NetworkCategory.Unknown)]
    public void The_windows_category_numbers_map_to_the_enum(int nlm, NetworkCategory expected) =>
        Assert.Equal(expected, NetworkListManagerProfileInfo.FromNlm(nlm));

    [Fact]
    public async Task The_real_provider_answers_unknown_for_an_address_no_adapter_owns()
    {
        // 192.0.2.0/24 is reserved for documentation (RFC 5737): no adapter can have it.
        var category = await new NetworkListManagerProfileInfo().GetCategoryAsync(IPAddress.Parse("192.0.2.123"));

        Assert.Equal(NetworkCategory.Unknown, category);
    }
}
