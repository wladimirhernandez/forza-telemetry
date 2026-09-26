namespace ForzaTelemetry.Core;

/// <summary>
/// A single, fully-decoded Forza Horizon 6 "Data Out" UDP telemetry packet.
/// </summary>
/// <remarks>
/// Field names, types and order mirror the official spec:
/// https://support.forza.net/hc/en-us/articles/51744149102611-Forza-Horizon-6-Data-Out-Documentation
/// (archived copy used: http://web.archive.org/web/20260629062150/https://support.forza.net/hc/en-us/articles/51744149102611-Forza-Horizon-6-Data-Out-Documentation)
///
/// FH6 inserts three fields that Forza Motorsport does not have: <see cref="CarGroup"/>,
/// <see cref="SmashableVelDiff"/> and <see cref="SmashableMass"/>. They sit between
/// <see cref="NumCylinders"/> and <see cref="PositionX"/> — get that placement wrong and every
/// field after it decodes garbage, which is why the parser tests specifically pin down the
/// bytes around this block.
///
/// This is an immutable record: a decoded packet is a snapshot of one instant of the simulation,
/// never mutated after parsing, so a record with init-only properties is a better fit than a
/// mutable class.
/// </remarks>
public sealed record TelemetryPacket
{
    // ---- Race status --------------------------------------------------

    /// <summary>
    /// Whether the game is actively simulating a drive (true) or sitting in a menu/paused/stopped
    /// state (false). The wire format is a 32-bit signed integer (1 or 0); it is exposed here as
    /// <see cref="bool"/> because that is what every call site actually wants, and the parser is
    /// the only place that needs to know about the raw S32 encoding.
    /// </summary>
    public required bool IsRaceOn { get; init; }

    /// <summary>Milliseconds since the game started sending data. Documented to overflow back to 0 eventually.</summary>
    public required uint TimestampMS { get; init; }

    // ---- Engine ---------------------------------------------------------

    public required float EngineMaxRpm { get; init; }
    public required float EngineIdleRpm { get; init; }
    public required float CurrentEngineRpm { get; init; }

    // ---- Motion (car-local space unless noted) --------------------------

    /// <summary>Car-local acceleration; X = right, Y = up, Z = forward.</summary>
    public required float AccelerationX { get; init; }
    public required float AccelerationY { get; init; }
    public required float AccelerationZ { get; init; }

    /// <summary>Car-local velocity; X = right, Y = up, Z = forward.</summary>
    public required float VelocityX { get; init; }
    public required float VelocityY { get; init; }
    public required float VelocityZ { get; init; }

    /// <summary>Angular velocity in rad/s; X = pitch, Y = yaw, Z = roll.</summary>
    public required float AngularVelocityX { get; init; }
    public required float AngularVelocityY { get; init; }
    public required float AngularVelocityZ { get; init; }

    /// <summary>Car orientation, in radians.</summary>
    public required float Yaw { get; init; }
    public required float Pitch { get; init; }
    public required float Roll { get; init; }

    // ---- Wheels / tires (suspension, slip, rumble) -----------------------

    /// <summary>Normalized suspension travel: 0.0 = max stretch, 1.0 = max compression.</summary>
    public required float NormalizedSuspensionTravelFrontLeft { get; init; }
    public required float NormalizedSuspensionTravelFrontRight { get; init; }
    public required float NormalizedSuspensionTravelRearLeft { get; init; }
    public required float NormalizedSuspensionTravelRearRight { get; init; }

    /// <summary>Normalized tire slip ratio: 0 = full grip, |ratio| &gt; 1.0 = loss of grip.</summary>
    public required float TireSlipRatioFrontLeft { get; init; }
    public required float TireSlipRatioFrontRight { get; init; }
    public required float TireSlipRatioRearLeft { get; init; }
    public required float TireSlipRatioRearRight { get; init; }

    /// <summary>Wheel rotation speed, radians/sec.</summary>
    public required float WheelRotationSpeedFrontLeft { get; init; }
    public required float WheelRotationSpeedFrontRight { get; init; }
    public required float WheelRotationSpeedRearLeft { get; init; }
    public required float WheelRotationSpeedRearRight { get; init; }

    /// <summary>1 when the wheel is on a rumble strip, 0 otherwise. Kept as S32 (not bool) to match
    /// the wire type exactly, since the spec does not single these out the way it does IsRaceOn.</summary>
    public required int WheelOnRumbleStripFrontLeft { get; init; }
    public required int WheelOnRumbleStripFrontRight { get; init; }
    public required int WheelOnRumbleStripRearLeft { get; init; }
    public required int WheelOnRumbleStripRearRight { get; init; }

    /// <summary>1 when the wheel is in a puddle, 0 otherwise.</summary>
    public required int WheelInPuddleFrontLeft { get; init; }
    public required int WheelInPuddleFrontRight { get; init; }
    public required int WheelInPuddleRearLeft { get; init; }
    public required int WheelInPuddleRearRight { get; init; }

    /// <summary>Non-dimensional surface rumble value passed to controller force feedback.</summary>
    public required float SurfaceRumbleFrontLeft { get; init; }
    public required float SurfaceRumbleFrontRight { get; init; }
    public required float SurfaceRumbleRearLeft { get; init; }
    public required float SurfaceRumbleRearRight { get; init; }

    /// <summary>Normalized tire slip angle: 0 = full grip, |angle| &gt; 1.0 = loss of grip.</summary>
    public required float TireSlipAngleFrontLeft { get; init; }
    public required float TireSlipAngleFrontRight { get; init; }
    public required float TireSlipAngleRearLeft { get; init; }
    public required float TireSlipAngleRearRight { get; init; }

    /// <summary>Normalized combined (ratio + angle) tire slip: 0 = full grip, |slip| &gt; 1.0 = loss of grip.</summary>
    public required float TireCombinedSlipFrontLeft { get; init; }
    public required float TireCombinedSlipFrontRight { get; init; }
    public required float TireCombinedSlipRearLeft { get; init; }
    public required float TireCombinedSlipRearRight { get; init; }

    /// <summary>Actual suspension travel, in meters.</summary>
    public required float SuspensionTravelMetersFrontLeft { get; init; }
    public required float SuspensionTravelMetersFrontRight { get; init; }
    public required float SuspensionTravelMetersRearLeft { get; init; }
    public required float SuspensionTravelMetersRearRight { get; init; }

    // ---- Car info (includes the FH6-only block) --------------------------

    /// <summary>Unique ID of the car make/model.</summary>
    public required int CarOrdinal { get; init; }

    /// <summary>Car class, 0 (D — worst) to 7 (X — best) inclusive.</summary>
    public required int CarClass { get; init; }

    /// <summary>Car performance index, 100 (worst) to 999 (best) inclusive.</summary>
    public required int CarPerformanceIndex { get; init; }

    /// <summary>Drivetrain layout: 0 = FWD, 1 = RWD, 2 = AWD.</summary>
    public required int DrivetrainType { get; init; }

    /// <summary>Number of cylinders in the engine.</summary>
    public required int NumCylinders { get; init; }

    /// <summary>
    /// FH6-only field. Car group identifier. Inserted, together with <see cref="SmashableVelDiff"/>
    /// and <see cref="SmashableMass"/>, right after <see cref="NumCylinders"/> and before
    /// <see cref="PositionX"/> — a placement Forza Motorsport does not have.
    /// </summary>
    public required uint CarGroup { get; init; }

    /// <summary>FH6-only field. Velocity lost from a smashable-object collision, in m/s.</summary>
    public required float SmashableVelDiff { get; init; }

    /// <summary>FH6-only field. Mass of the most recently hit smashable object, in kg.</summary>
    public required float SmashableMass { get; init; }

    // ---- Position / dynamics summary -------------------------------------

    /// <summary>Position in world space, meters.</summary>
    public required float PositionX { get; init; }
    public required float PositionY { get; init; }
    public required float PositionZ { get; init; }

    /// <summary>Speed, in meters per second. See <see cref="SpeedKmh"/> for the km/h view.</summary>
    public required float Speed { get; init; }

    /// <summary>Power, in watts.</summary>
    public required float Power { get; init; }

    /// <summary>Torque, in newton-meters.</summary>
    public required float Torque { get; init; }

    // ---- Wheels / tires (temperature) -------------------------------------

    public required float TireTempFrontLeft { get; init; }
    public required float TireTempFrontRight { get; init; }
    public required float TireTempRearLeft { get; init; }
    public required float TireTempRearRight { get; init; }

    // ---- Engine (forced induction / fuel) ---------------------------------

    /// <summary>Turbo/supercharger boost, PSI above atmospheric.</summary>
    public required float Boost { get; init; }

    /// <summary>Fuel level: 0.0 = empty, 1.0 = full.</summary>
    public required float Fuel { get; init; }

    // ---- Race -------------------------------------------------------------

    /// <summary>Total distance traveled, in meters.</summary>
    public required float DistanceTraveled { get; init; }

    /// <summary>Best lap time, seconds. 0.0 if not applicable.</summary>
    public required float BestLap { get; init; }

    /// <summary>Last lap time, seconds. 0.0 if not applicable.</summary>
    public required float LastLap { get; init; }

    /// <summary>Current (in-progress) lap time, seconds. 0.0 if not applicable.</summary>
    public required float CurrentLap { get; init; }

    /// <summary>Total race time, seconds since driving started.</summary>
    public required float CurrentRaceTime { get; init; }

    /// <summary>Number of laps completed.</summary>
    public required ushort LapNumber { get; init; }

    /// <summary>Current race position.</summary>
    public required byte RacePosition { get; init; }

    // ---- Inputs -------------------------------------------------------------

    /// <summary>Accelerator pedal input, 0 to 255.</summary>
    public required byte Accel { get; init; }

    /// <summary>Brake pedal input, 0 to 255.</summary>
    public required byte Brake { get; init; }

    /// <summary>Clutch pedal input, 0 to 255.</summary>
    public required byte Clutch { get; init; }

    /// <summary>Handbrake input, 0 to 255.</summary>
    public required byte HandBrake { get; init; }

    /// <summary>Current gear.</summary>
    public required byte Gear { get; init; }

    /// <summary>Steering input: -127 = full left, 0 = center, 127 = full right. Signed, hence sbyte.</summary>
    public required sbyte Steer { get; init; }

    /// <summary>Normalized driving line position, -127 to 127.</summary>
    public required sbyte NormalizedDrivingLine { get; init; }

    /// <summary>Normalized AI braking difference, -127 to 127.</summary>
    public required sbyte NormalizedAIBrakeDifference { get; init; }

    // ---- Derived helpers ---------------------------------------------------

    /// <summary>
    /// <see cref="Speed"/> converted from meters per second to kilometers per hour
    /// (the spec only gives m/s; this is a trivial, commonly-wanted display conversion).
    /// </summary>
    public float SpeedKmh => Speed * 3.6f;
}
