namespace ForzaTelemetry.Udp;

/// <summary>
/// Configures the "have we gone quiet?" heartbeat that <see cref="UdpTelemetryListener"/> logs
/// while it is waiting for datagrams.
/// </summary>
/// <remarks>
/// Exposed as a record with constructor parameters (not baked-in constants) specifically so tests
/// can pass very short thresholds - e.g. 200 ms instead of the production 5 s/10 s - and observe the
/// heartbeat behavior in a fraction of a second instead of waiting on a real wall-clock timer.
/// </remarks>
/// <param name="InitialSilence">
/// How long to wait, from listener startup or from the last received datagram, before logging the
/// first "no datagrams received" message.
/// </param>
/// <param name="RepeatInterval">
/// How much additional silence to wait before logging the message again. Repeats for as long as the
/// silence continues, so the user does not have to guess whether the listener is still alive.
/// </param>
public sealed record HeartbeatOptions(TimeSpan InitialSilence, TimeSpan RepeatInterval)
{
    /// <summary>
    /// Production defaults: first warning after 5 seconds of silence, then every further 10 seconds
    /// for as long as nothing arrives.
    /// </summary>
    public static HeartbeatOptions Default { get; } = new(
        InitialSilence: TimeSpan.FromSeconds(5),
        RepeatInterval: TimeSpan.FromSeconds(10));
}
