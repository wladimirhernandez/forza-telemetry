namespace ForzaTelemetry.Core.Insights;

/// <summary>
/// A compact, normalized subset of a packet kept for time-series charts.
/// </summary>
/// <remarks>
/// A struct (not the 90-field <see cref="TelemetryPacket"/> record) so a ring buffer of thousands of samples is
/// one contiguous array with no per-sample heap object: ~60 s at 60 Hz is 3600 samples of ~100 bytes, and the
/// garbage collector never has to trace them. Raw wire bytes are converted to chart-friendly units here once
/// (pedals 0..1, steering -1..1) instead of in every renderer.
/// </remarks>
public readonly struct TelemetrySample
{
    /// <summary>Game time in milliseconds.</summary>
    public uint TimeMs { get; init; }

    /// <summary>Speed in meters per second.</summary>
    public float SpeedMs { get; init; }

    public float Rpm { get; init; }

    public float MaxRpm { get; init; }

    /// <summary>Throttle 0..1.</summary>
    public float Throttle { get; init; }

    /// <summary>Brake 0..1.</summary>
    public float Brake { get; init; }

    /// <summary>Steering -1 (full left) .. 1 (full right).</summary>
    public float Steer { get; init; }

    public byte Gear { get; init; }

    public float PositionX { get; init; }

    public float PositionZ { get; init; }

    public float SlipAngleFrontLeft { get; init; }
    public float SlipAngleFrontRight { get; init; }
    public float SlipAngleRearLeft { get; init; }
    public float SlipAngleRearRight { get; init; }

    /// <summary>Normalized suspension travel, 0 = fully extended, 1 = fully compressed.</summary>
    public float SuspensionFrontLeft { get; init; }
    public float SuspensionFrontRight { get; init; }
    public float SuspensionRearLeft { get; init; }
    public float SuspensionRearRight { get; init; }

    /// <summary>Tire temperatures in the wire unit (see TireTemperatureScale in the app).</summary>
    public float TireTempFrontLeft { get; init; }
    public float TireTempFrontRight { get; init; }
    public float TireTempRearLeft { get; init; }
    public float TireTempRearRight { get; init; }

    /// <summary>Converts a packet to a sample.</summary>
    public static TelemetrySample FromPacket(TelemetryPacket p) => new()
    {
        TimeMs = p.TimestampMS,
        SpeedMs = p.Speed,
        Rpm = p.CurrentEngineRpm,
        MaxRpm = p.EngineMaxRpm,
        Throttle = p.Accel / 255f,
        Brake = p.Brake / 255f,
        Steer = Math.Clamp(p.Steer / 127f, -1f, 1f),
        Gear = p.Gear,
        PositionX = p.PositionX,
        PositionZ = p.PositionZ,
        SlipAngleFrontLeft = p.TireSlipAngleFrontLeft,
        SlipAngleFrontRight = p.TireSlipAngleFrontRight,
        SlipAngleRearLeft = p.TireSlipAngleRearLeft,
        SlipAngleRearRight = p.TireSlipAngleRearRight,
        SuspensionFrontLeft = p.NormalizedSuspensionTravelFrontLeft,
        SuspensionFrontRight = p.NormalizedSuspensionTravelFrontRight,
        SuspensionRearLeft = p.NormalizedSuspensionTravelRearLeft,
        SuspensionRearRight = p.NormalizedSuspensionTravelRearRight,
        TireTempFrontLeft = p.TireTempFrontLeft,
        TireTempFrontRight = p.TireTempFrontRight,
        TireTempRearLeft = p.TireTempRearLeft,
        TireTempRearRight = p.TireTempRearRight,
    };
}
