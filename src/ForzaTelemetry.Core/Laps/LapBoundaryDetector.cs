namespace ForzaTelemetry.Core.Laps;

/// <summary>What a packet did to the lap bookkeeping, relative to the packets before it.</summary>
internal enum LapBoundary
{
    /// <summary>Nothing: the lap in progress simply continues.</summary>
    None,

    /// <summary>The first packet this detector sees (or the first after a reset).</summary>
    FirstPacket,

    /// <summary>The lap counter went down: the race restarted and every lap seen so far is history.</summary>
    RaceRestarted,

    /// <summary>The lap counter went up: the lap in progress ended.</summary>
    LapCompleted,

    /// <summary>Same lap number but the lap clock jumped back: the player restarted the lap.</summary>
    LapRestarted,
}

/// <summary>The lap that just ended, as far as the packet that ended it tells.</summary>
/// <param name="Skipped">More than one lap completed between two packets (a dropped burst, a pause: 3 -> 5): the samples in hand are NOT this lap's.</param>
/// <param name="Time">Seconds the lap took; 0 when unknown.</param>
internal readonly record struct FinishedLap(bool Skipped, double Time);

/// <summary>
/// The lap-boundary rules, in one place, for everything that records a lap: when a lap starts, ends or restarts, whether the lap in
/// progress can ever be a reference ("best lap"), and which samples of it are kept.
/// </summary>
/// <remarks>
/// <para><b>Why shared.</b> <see cref="LapTracker"/> (the live delta) and <see cref="LapTraceRecorder"/> (the comparison chart) must
/// agree on which lap is the best one, or the page's "Gap" tile and the chart's gap lane would compare against different laps.
/// Two copies of these rules would drift apart the first time one of them was fixed; one detector cannot.</para>
///
/// <para><b>What it does not own.</b> The samples themselves (each recorder keeps its own arrays, with its own payload), the lap list
/// and the "faster than the previous best" decision. It only answers the questions the two have in common.</para>
///
/// <para><b>Not thread-safe</b>: the owner calls it under its own lock.</para>
/// </remarks>
internal sealed class LapBoundaryDetector
{
    /// <summary>A lap seen from its start has a CurrentLap below this many seconds on the first packet.</summary>
    internal const double FullLapStartToleranceSeconds = 1.0;

    /// <summary>Minimum distance between recorded samples, meters.</summary>
    internal const double SampleSpacingMeters = 1.0;

    /// <summary>CurrentLap dropping by more than this within the same lap number means the lap was restarted.</summary>
    internal const double RestartDropSeconds = 1.0;

    /// <summary>A packet has been seen since the last <see cref="Reset"/>.</summary>
    public bool HasPrevious { get; private set; }

    /// <summary>Laps completed, as the game counts them (the lap in progress is this plus one).</summary>
    public int LapsCompleted { get; private set; }

    /// <summary>The lap clock of the previous packet.</summary>
    public double PreviousCurrentLap { get; private set; }

    /// <summary>Odometer reading at the start of the lap in progress.</summary>
    public double LapStartDistance { get; private set; }

    /// <summary>The lap in progress was seen from its start and fully recorded so far, so it may become a reference lap.</summary>
    public bool CurrentLapValid { get; private set; }

    /// <summary>Forgets everything, as if no packet had ever arrived.</summary>
    public void Reset()
    {
        HasPrevious = false;
        LapsCompleted = 0;
        PreviousCurrentLap = 0;
        LapStartDistance = 0;
        CurrentLapValid = false;
    }

    /// <summary>Says what the packet is, without changing anything: the owner reacts, then calls <see cref="StartLap"/> and <see cref="Advance"/>.</summary>
    public LapBoundary Classify(TelemetryPacket packet)
    {
        if (!HasPrevious)
        {
            return LapBoundary.FirstPacket;
        }

        if (packet.LapNumber < LapsCompleted)
        {
            return LapBoundary.RaceRestarted;
        }

        if (packet.LapNumber > LapsCompleted)
        {
            return LapBoundary.LapCompleted;
        }

        return packet.CurrentLap < PreviousCurrentLap - RestartDropSeconds ? LapBoundary.LapRestarted : LapBoundary.None;
    }

    /// <summary>
    /// The lap a <see cref="LapBoundary.LapCompleted"/> packet ended, and how long it took: the game's LastLap, else the last lap
    /// clock seen, unless laps were skipped (then the lap in progress is no fallback for the reported lap's time).
    /// </summary>
    public FinishedLap Finish(TelemetryPacket packet)
    {
        // More than one lap completed since the last packet we saw (a dropped burst, a pause: 3 -> 5). The lap the packet reports
        // (LastLap, number LapNumber) is NOT the one whose clock and samples we hold: those belong to the lap in progress before the
        // jump. So its samples must never become a reference, and the lap in progress is no fallback for the reported lap's time.
        var skipped = packet.LapNumber - LapsCompleted > 1;
        double time = packet.LastLap > 0 ? packet.LastLap : skipped ? 0 : PreviousCurrentLap;
        return new FinishedLap(skipped, time);
    }

    /// <summary>
    /// The lap in progress, which has <paramref name="sampleCount"/> samples, may be the reference lap if it ended as
    /// <paramref name="finished"/>: it has a known time, its samples are really that lap's, it was seen from its start, and it has
    /// enough samples to interpolate. (Beating the previous best is the owner's check: it knows its own best.)
    /// </summary>
    public bool CanBeReference(FinishedLap finished, int sampleCount) =>
        !finished.Skipped && finished.Time > 0 && CurrentLapValid && sampleCount >= 2;

    /// <summary>A new lap begins at this packet.</summary>
    public void StartLap(int lapsCompleted, double distance, double currentLap)
    {
        HasPrevious = true;
        LapsCompleted = lapsCompleted;
        LapStartDistance = distance;
        CurrentLapValid = currentLap <= FullLapStartToleranceSeconds;
        PreviousCurrentLap = currentLap;
    }

    /// <summary>The packet has been handled: its lap clock is what the next one is compared with.</summary>
    public void Advance(double currentLap) => PreviousCurrentLap = currentLap;

    /// <summary>
    /// Whether a packet at odometer reading <paramref name="distance"/> adds a sample to a lap that already has
    /// <paramref name="count"/> of them, the last at <paramref name="lastSampleDistance"/> meters into the lap. One per metre, none
    /// before the lap's start, and none once <see cref="LapTracker.MaxSamplesPerLap"/> are kept: then the lap is marked unusable as
    /// a reference (free roam, or an absurdly long lap, is not a real lap).
    /// </summary>
    public bool ShouldRecord(double distance, int count, double lastSampleDistance)
    {
        var distanceIntoLap = distance - LapStartDistance;
        if (distanceIntoLap < 0)
        {
            return false;
        }

        if (count > 0 && distanceIntoLap - lastSampleDistance < SampleSpacingMeters)
        {
            return false;
        }

        if (count >= LapTracker.MaxSamplesPerLap)
        {
            CurrentLapValid = false;
            return false;
        }

        return true;
    }
}
