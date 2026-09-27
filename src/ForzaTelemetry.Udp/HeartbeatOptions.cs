namespace ForzaTelemetry.Udp;

/// <summary>
/// Configures the "have we gone quiet?" heartbeat that <see cref="UdpTelemetryListener"/> logs
/// while it is waiting for datagrams.
/// </summary>
/// <remarks>
/// Exposed as a record with constructor parameters (not baked-in constants) specifically so tests
/// can pass very short thresholds - e.g. 200 ms instead of the production 5 s/10 s - and observe the
/// heartbeat behavior in a fraction of a second instead of waiting on a real wall-clock timer.
///
/// Both thresholds are validated in the constructor (see <see cref="Validate"/>) rather than left
/// to fail later, deep inside <see cref="Task.Delay(TimeSpan, System.Threading.CancellationToken)"/>:
/// a non-positive value would make <see cref="UdpTelemetryListener"/>'s receive loop hot-loop
/// (racing an already-elapsed delay on every single iteration), and a value near
/// <see cref="TimeSpan.MaxValue"/> overflows the internal timer <c>Task.Delay</c> itself uses.
/// Failing fast in the constructor turns both mistakes into an immediate, clearly-worded
/// <see cref="ArgumentOutOfRangeException"/> instead of a confusing runtime failure or a silently
/// wrong (spinning) listener.
/// </remarks>
/// <param name="InitialSilence">
/// How long to wait, from listener startup or from the last received datagram, before logging the
/// first "no datagrams received" message. Must be greater than zero and at most
/// <see cref="MaxThreshold"/>.
/// </param>
/// <param name="RepeatInterval">
/// How much additional silence to wait before logging the message again. Repeats for as long as the
/// silence continues, so the user does not have to guess whether the listener is still alive. Must
/// be greater than zero and at most <see cref="MaxThreshold"/>.
/// </param>
public sealed record HeartbeatOptions
{
    /// <summary>
    /// Upper bound accepted for both <see cref="InitialSilence"/> and <see cref="RepeatInterval"/>.
    /// </summary>
    /// <remarks>
    /// Chosen well under <c>Task.Delay</c>'s own absolute limit (about 49.7 days, i.e.
    /// <c>TimeSpan.FromMilliseconds(uint.MaxValue - 1)</c>): nobody plays Forza Horizon 6 in one
    /// sitting for anywhere close to a full day, so a value that large almost certainly comes from
    /// a mistake (e.g. an accidental extra zero, or an unconverted unit) rather than an intentional
    /// configuration. Capping at one day catches that mistake immediately, with a clear message,
    /// instead of only failing much later - or not at all - deep inside the framework.
    /// </remarks>
    public static readonly TimeSpan MaxThreshold = TimeSpan.FromDays(1);

    /// <summary>See the parameter doc comment above.</summary>
    public TimeSpan InitialSilence { get; }

    /// <summary>See the parameter doc comment above.</summary>
    public TimeSpan RepeatInterval { get; }

    public HeartbeatOptions(TimeSpan InitialSilence, TimeSpan RepeatInterval)
    {
        this.InitialSilence = Validate(InitialSilence, nameof(InitialSilence));
        this.RepeatInterval = Validate(RepeatInterval, nameof(RepeatInterval));
    }

    /// <summary>
    /// Production defaults: first warning after 5 seconds of silence, then every further 10 seconds
    /// for as long as nothing arrives.
    /// </summary>
    public static HeartbeatOptions Default { get; } = new(
        InitialSilence: TimeSpan.FromSeconds(5),
        RepeatInterval: TimeSpan.FromSeconds(10));

    // A dedicated "Disabled" option (skipping Task.Delay entirely) was considered here, but it
    // would require UdpTelemetryListener's receive loop to branch around the heartbeat race
    // instead of just racing a very long delay - real added complexity for a feature nothing in
    // this codebase currently needs. Nobody wants the heartbeat literally disabled today; passing a
    // long-but-valid threshold (e.g. MaxThreshold itself) already makes it effectively silent for
    // any realistic play session, without touching the receive loop at all.
    private static TimeSpan Validate(TimeSpan value, string paramName)
    {
        if (value <= TimeSpan.Zero || value > MaxThreshold)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                value,
                $"Must be greater than zero and at most {MaxThreshold} (Task.Delay's practical limit for this listener's heartbeat).");
        }

        return value;
    }
}
