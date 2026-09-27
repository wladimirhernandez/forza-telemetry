using ForzaTelemetry.Core;

namespace ForzaTelemetry.App.Tests;

/// <summary>
/// <see cref="TelemetryPacket"/> mirrors the wire format 1:1, which means it has roughly ninety
/// `required` properties (see its own doc comments) — no test should ever construct one
/// property-by-property. This single factory gives every test a valid, fully-populated packet with
/// harmless zero/default values, overriding only the handful of fields (RPM, speed, gear) a given
/// test actually cares about.
/// </summary>
internal static class TestPackets
{
    public static TelemetryPacket Sample(
        float currentEngineRpm = 4_500f,
        float engineMaxRpm = 7_000f,
        float speedMetersPerSecond = 30f,
        byte gear = 3) => new()
    {
        IsRaceOn = true,
        TimestampMS = 0,

        EngineMaxRpm = engineMaxRpm,
        EngineIdleRpm = 900f,
        CurrentEngineRpm = currentEngineRpm,

        AccelerationX = 0,
        AccelerationY = 0,
        AccelerationZ = 0,
        VelocityX = 0,
        VelocityY = 0,
        VelocityZ = 0,
        AngularVelocityX = 0,
        AngularVelocityY = 0,
        AngularVelocityZ = 0,
        Yaw = 0,
        Pitch = 0,
        Roll = 0,

        NormalizedSuspensionTravelFrontLeft = 0,
        NormalizedSuspensionTravelFrontRight = 0,
        NormalizedSuspensionTravelRearLeft = 0,
        NormalizedSuspensionTravelRearRight = 0,
        TireSlipRatioFrontLeft = 0,
        TireSlipRatioFrontRight = 0,
        TireSlipRatioRearLeft = 0,
        TireSlipRatioRearRight = 0,
        WheelRotationSpeedFrontLeft = 0,
        WheelRotationSpeedFrontRight = 0,
        WheelRotationSpeedRearLeft = 0,
        WheelRotationSpeedRearRight = 0,
        WheelOnRumbleStripFrontLeft = 0,
        WheelOnRumbleStripFrontRight = 0,
        WheelOnRumbleStripRearLeft = 0,
        WheelOnRumbleStripRearRight = 0,
        WheelInPuddleFrontLeft = 0,
        WheelInPuddleFrontRight = 0,
        WheelInPuddleRearLeft = 0,
        WheelInPuddleRearRight = 0,
        SurfaceRumbleFrontLeft = 0,
        SurfaceRumbleFrontRight = 0,
        SurfaceRumbleRearLeft = 0,
        SurfaceRumbleRearRight = 0,
        TireSlipAngleFrontLeft = 0,
        TireSlipAngleFrontRight = 0,
        TireSlipAngleRearLeft = 0,
        TireSlipAngleRearRight = 0,
        TireCombinedSlipFrontLeft = 0,
        TireCombinedSlipFrontRight = 0,
        TireCombinedSlipRearLeft = 0,
        TireCombinedSlipRearRight = 0,
        SuspensionTravelMetersFrontLeft = 0,
        SuspensionTravelMetersFrontRight = 0,
        SuspensionTravelMetersRearLeft = 0,
        SuspensionTravelMetersRearRight = 0,

        CarOrdinal = 0,
        CarClass = 0,
        CarPerformanceIndex = 0,
        DrivetrainType = 1,
        NumCylinders = 4,

        CarGroup = 0,
        SmashableVelDiff = 0,
        SmashableMass = 0,

        PositionX = 0,
        PositionY = 0,
        PositionZ = 0,
        Speed = speedMetersPerSecond,
        Power = 0,
        Torque = 0,

        TireTempFrontLeft = 0,
        TireTempFrontRight = 0,
        TireTempRearLeft = 0,
        TireTempRearRight = 0,

        Boost = 0,
        Fuel = 1,

        DistanceTraveled = 0,
        BestLap = 0,
        LastLap = 0,
        CurrentLap = 0,
        CurrentRaceTime = 0,
        LapNumber = 0,
        RacePosition = 0,

        Accel = 0,
        Brake = 0,
        Clutch = 0,
        HandBrake = 0,
        Gear = gear,
        Steer = 0,
        NormalizedDrivingLine = 0,
        NormalizedAIBrakeDifference = 0
    };
}
