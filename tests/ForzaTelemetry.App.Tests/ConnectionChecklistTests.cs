using ForzaTelemetry.App.Services;

namespace ForzaTelemetry.App.Tests;

/// <summary>The state machine of the "Connect your game" checklist: one pure function, so every row is a plain table case.</summary>
public class ConnectionChecklistTests
{
    private static readonly TimeSpan Silent = ConnectionChecklist.TroubleshootingDelay;

    private static ConnectionSignals Signals(
        bool listening = true,
        bool replaying = false,
        bool fault = false,
        NetworkCategory network = NetworkCategory.Private,
        bool sender = false,
        bool packets = false,
        bool driven = false,
        TimeSpan? listeningFor = null) =>
        new(listening, replaying, fault, network, sender, packets, driven, listeningFor ?? TimeSpan.Zero);

    [Fact]
    public void Nothing_running_means_everything_is_pending()
    {
        var result = ConnectionChecklist.Evaluate(Signals(listening: false, network: NetworkCategory.Unknown));

        Assert.Equal(CheckState.Pending, result.Listening);
        Assert.Equal(CheckState.Pending, result.Network);
        Assert.Equal(CheckState.Pending, result.Packets);
        Assert.Equal(CheckState.Pending, result.Driving);
        Assert.False(result.IsConnected);
        Assert.False(result.ShowTroubleshooting);
    }

    [Fact]
    public void Listening_with_no_data_yet_is_ok_then_pending_pending()
    {
        var result = ConnectionChecklist.Evaluate(Signals(listeningFor: TimeSpan.FromSeconds(3)));

        Assert.Equal(CheckState.Ok, result.Listening);
        Assert.Equal(CheckState.Ok, result.Network);
        Assert.Equal(CheckState.Pending, result.Packets);
        Assert.Equal(CheckState.Pending, result.Driving);
        Assert.False(result.ShowTroubleshooting); // not long enough to say anything is wrong
    }

    [Fact]
    public void A_listener_fault_is_a_warning_on_the_first_line()
    {
        var result = ConnectionChecklist.Evaluate(Signals(listening: false, fault: true));

        Assert.Equal(CheckState.Warning, result.Listening);
        Assert.False(result.IsConnected);
    }

    [Fact]
    public void A_replay_is_not_listening_to_the_game()
    {
        var result = ConnectionChecklist.Evaluate(Signals(replaying: true, sender: true, packets: true, driven: true));

        Assert.Equal(CheckState.Pending, result.Listening);
        Assert.Equal(CheckState.Pending, result.Packets); // a recording's packets do not count as "arriving"
        Assert.Equal(CheckState.Pending, result.Driving);
        Assert.False(result.IsConnected);
    }

    [Fact]
    public void A_public_network_warns_until_data_gets_through_anyway()
    {
        Assert.Equal(CheckState.Warning, ConnectionChecklist.Evaluate(Signals(network: NetworkCategory.Public)).Network);
        Assert.Equal(CheckState.Ok, ConnectionChecklist.Evaluate(Signals(network: NetworkCategory.Public, sender: true)).Network);
    }

    [Theory]
    [InlineData(NetworkCategory.Private, CheckState.Ok)]
    [InlineData(NetworkCategory.Domain, CheckState.Ok)]
    [InlineData(NetworkCategory.Unknown, CheckState.Pending)]
    public void Other_profiles_are_not_a_problem(NetworkCategory network, CheckState expected) =>
        Assert.Equal(expected, ConnectionChecklist.Evaluate(Signals(network: network)).Network);

    [Fact]
    public void A_datagram_from_anyone_is_enough_for_the_packets_line()
    {
        // Even one the parser would reject (wrong size): the network path works, which is what this line proves.
        var result = ConnectionChecklist.Evaluate(Signals(sender: true));

        Assert.Equal(CheckState.Ok, result.Packets);
        Assert.Equal(CheckState.Pending, result.Driving);
    }

    [Fact]
    public void Parsed_packets_count_even_without_a_known_sender()
    {
        Assert.Equal(CheckState.Ok, ConnectionChecklist.Evaluate(Signals(packets: true)).Packets);
    }

    [Fact]
    public void Menu_packets_prove_the_link_but_not_driving()
    {
        var result = ConnectionChecklist.Evaluate(Signals(sender: true, packets: true, driven: false));

        Assert.Equal(CheckState.Ok, result.Packets);
        Assert.Equal(CheckState.Pending, result.Driving);
        Assert.False(result.IsConnected);
    }

    [Fact]
    public void Everything_done_is_connected()
    {
        var result = ConnectionChecklist.Evaluate(Signals(sender: true, packets: true, driven: true));

        Assert.True(result.IsConnected);
        Assert.False(result.ShowTroubleshooting);
    }

    [Theory]
    [InlineData(NetworkCategory.Unknown)]
    [InlineData(NetworkCategory.Public)]
    public void An_unreadable_or_public_network_never_keeps_a_working_setup_from_being_connected(NetworkCategory network)
    {
        Assert.True(ConnectionChecklist.Evaluate(Signals(network: network, sender: true, packets: true, driven: true)).IsConnected);
    }

    [Fact]
    public void Twenty_seconds_of_silence_turns_packets_into_a_warning_and_shows_the_troubleshooting_list()
    {
        var justBefore = ConnectionChecklist.Evaluate(Signals(listeningFor: Silent - TimeSpan.FromMilliseconds(1)));
        var atTheLimit = ConnectionChecklist.Evaluate(Signals(listeningFor: Silent));

        Assert.Equal(CheckState.Pending, justBefore.Packets);
        Assert.False(justBefore.ShowTroubleshooting);
        Assert.Equal(CheckState.Warning, atTheLimit.Packets);
        Assert.True(atTheLimit.ShowTroubleshooting);
    }

    [Fact]
    public void Silence_hints_never_appear_once_data_arrives_or_when_not_listening()
    {
        Assert.False(ConnectionChecklist.Evaluate(Signals(sender: true, listeningFor: TimeSpan.FromMinutes(5))).ShowTroubleshooting);
        Assert.False(ConnectionChecklist.Evaluate(Signals(listening: false, listeningFor: TimeSpan.FromMinutes(5))).ShowTroubleshooting);
    }
}
