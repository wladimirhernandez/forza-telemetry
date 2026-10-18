namespace ForzaTelemetry.Core.Laps;

/// <summary>One finished lap.</summary>
/// <param name="Number">1-based lap number.</param>
/// <param name="TimeSeconds">Lap time in seconds, as reported by the game.</param>
/// <param name="IsBest">True when this lap was the fastest of the session at the moment it finished.</param>
public sealed record LapRecord(int Number, double TimeSeconds, bool IsBest);

/// <summary>An immutable picture of the lap tracker for UI readers.</summary>
/// <param name="CurrentLapNumber">1-based number of the lap in progress.</param>
/// <param name="CurrentLapSeconds">Time into the lap in progress (0 when not applicable).</param>
/// <param name="LastLapSeconds">Previous lap time, null when none yet.</param>
/// <param name="BestLapSeconds">Best lap time, null when none yet.</param>
/// <param name="DeltaSeconds">Live delta against the best lap at the same track distance; positive = slower. Null when unavailable.</param>
/// <param name="Laps">Completed laps of this session in order.</param>
public sealed record LapSnapshot(
    int CurrentLapNumber,
    double CurrentLapSeconds,
    double? LastLapSeconds,
    double? BestLapSeconds,
    double? DeltaSeconds,
    IReadOnlyList<LapRecord> Laps)
{
    /// <summary>The initial snapshot before any packet.</summary>
    public static LapSnapshot Empty { get; } = new(1, 0, null, null, null, Array.Empty<LapRecord>());
}
