using ForzaTelemetry.App.Services;

namespace ForzaTelemetry.App;

/// <summary>How far one line of the connection checklist has got.</summary>
public enum CheckState
{
    /// <summary>Not done yet (nothing is wrong, it is just waiting for something to happen).</summary>
    Pending = 0,

    /// <summary>Done.</summary>
    Ok,

    /// <summary>Something is wrong or needs the user's attention.</summary>
    Warning
}

/// <summary>Everything the checklist decides from: a plain snapshot, so the rules are testable without a clock, a socket or a window.</summary>
/// <param name="IsListening">A listener is running.</param>
/// <param name="IsReplaying">A recording is playing instead of the game.</param>
/// <param name="HasFault">The listener failed to start or died (port in use, socket error).</param>
/// <param name="Network">The Windows network profile of the address the game must send to.</param>
/// <param name="HasSender">At least one datagram arrived in this listening session (from anyone, valid or not).</param>
/// <param name="HasPackets">At least one valid telemetry packet was parsed in this listening session.</param>
/// <param name="HasDriven">A packet with <c>IsRaceOn</c> true was seen (the game is in a drive, not in a menu).</param>
/// <param name="ListeningFor">How long the listener has been running.</param>
public readonly record struct ConnectionSignals(
    bool IsListening,
    bool IsReplaying,
    bool HasFault,
    NetworkCategory Network,
    bool HasSender,
    bool HasPackets,
    bool HasDriven,
    TimeSpan ListeningFor);

/// <summary>The outcome of <see cref="ConnectionChecklist.Evaluate"/>.</summary>
/// <param name="Listening">Line 1: the app listens on the UDP port.</param>
/// <param name="Network">Line 2: the network profile is not a blocking Public one.</param>
/// <param name="Packets">Line 3: datagrams arrive from the game.</param>
/// <param name="Driving">Line 4: the game is in a drive.</param>
/// <param name="IsConnected">Lines 1, 3 and 4 are done: live driving data is flowing.</param>
/// <param name="ShowTroubleshooting">Listening for a while with nothing arriving: show the usual causes.</param>
public readonly record struct ChecklistResult(
    CheckState Listening,
    CheckState Network,
    CheckState Packets,
    CheckState Driving,
    bool IsConnected,
    bool ShowTroubleshooting);

/// <summary>The rules of the "Connect your game" checklist, as one pure function.</summary>
/// <remarks>
/// <b>Why the network line does not gate "connected".</b> The profile check is advice about the most common cause of silence
/// (a Public network blocks incoming UDP), not a requirement: if data is arriving, the network is evidently fine (a firewall
/// rule may allow it), so a Public profile stops being a warning the moment packets get through, and an unreadable profile
/// (<see cref="NetworkCategory.Unknown"/>) must never keep a working setup from saying "connected".
/// </remarks>
public static class ConnectionChecklist
{
    /// <summary>How long a running listener may stay silent before the troubleshooting list appears.</summary>
    public static readonly TimeSpan TroubleshootingDelay = TimeSpan.FromSeconds(20);

    /// <summary>Evaluates the four lines from the current signals.</summary>
    public static ChecklistResult Evaluate(ConnectionSignals signals)
    {
        var listening = signals.HasFault
            ? CheckState.Warning
            : signals.IsListening && !signals.IsReplaying ? CheckState.Ok : CheckState.Pending;

        // Without a live listener nothing the other lines would say is about the game: a replay's packets are not "arriving".
        var live = listening == CheckState.Ok;
        var packetsArrived = live && (signals.HasSender || signals.HasPackets);

        var network = signals.Network switch
        {
            NetworkCategory.Public => packetsArrived ? CheckState.Ok : CheckState.Warning,
            NetworkCategory.Private or NetworkCategory.Domain => CheckState.Ok,
            _ => CheckState.Pending
        };

        var silentTooLong = live && !packetsArrived && signals.ListeningFor >= TroubleshootingDelay;
        var packets = packetsArrived ? CheckState.Ok : silentTooLong ? CheckState.Warning : CheckState.Pending;
        var driving = live && signals.HasDriven ? CheckState.Ok : CheckState.Pending;

        return new ChecklistResult(
            listening,
            network,
            packets,
            driving,
            IsConnected: listening == CheckState.Ok && packets == CheckState.Ok && driving == CheckState.Ok,
            ShowTroubleshooting: silentTooLong);
    }
}
