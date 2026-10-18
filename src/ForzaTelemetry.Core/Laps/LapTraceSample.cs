namespace ForzaTelemetry.Core.Laps;

/// <summary>
/// One point of a lap trace: what the car was doing at a given distance into the lap.
/// </summary>
/// <remarks>
/// A struct with single-precision fields (about 28 bytes) so the two preallocated 6000-sample arrays of a
/// <see cref="LapTraceRecorder"/> are one contiguous block each, with no per-sample heap object. Single precision is
/// plenty: a distance of 6000 m resolves to under a millimetre, and a lap time to well under a millisecond. Pedals are
/// already 0..1 and steering -1..1, converted once here instead of in every renderer (same convention as
/// <see cref="Insights.TelemetrySample"/>).
/// </remarks>
public readonly struct LapTraceSample
{
    /// <summary>Distance driven since the lap began, in meters.</summary>
    public float DistanceMeters { get; init; }

    /// <summary>The lap clock at this point, in seconds (the game's own CurrentLap).</summary>
    public float TimeSeconds { get; init; }

    /// <summary>Speed in meters per second.</summary>
    public float SpeedMs { get; init; }

    /// <summary>Throttle 0..1.</summary>
    public float Throttle { get; init; }

    /// <summary>Brake 0..1.</summary>
    public float Brake { get; init; }

    /// <summary>Steering -1 (full left) .. 1 (full right).</summary>
    public float Steer { get; init; }

    /// <summary>The game's gear number.</summary>
    public byte Gear { get; init; }
}
