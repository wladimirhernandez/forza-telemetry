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
        byte gear = 3,
        float tempFrontLeft = 0,
        float tempFrontRight = 0,
        float tempRearLeft = 0,
        float tempRearRight = 0,
        float slipFrontLeft = 0,
        float suspensionFrontLeft = 0,
        float suspensionRearRight = 0,
        float accelerationX = 0,
        float accelerationZ = 0,
        byte accel = 0,
        byte brake = 0,
        byte clutch = 0,
        byte handBrake = 0,
        sbyte steer = 0,
        float power = 0,
        float torque = 0,
        float boost = 0,
        float fuel = 1,
        int carOrdinal = 0,
        int carClass = 0,
        int performanceIndex = 0,
        int drivetrain = 1) => new()
    {
        IsRaceOn = true,
        TimestampMS = 0,

        EngineMaxRpm = engineMaxRpm,
        EngineIdleRpm = 900f,
        CurrentEngineRpm = currentEngineRpm,

        AccelerationX = accelerationX,
        AccelerationY = 0,
        AccelerationZ = accelerationZ,
        VelocityX = 0,
        VelocityY = 0,
        VelocityZ = 0,
        AngularVelocityX = 0,
        AngularVelocityY = 0,
        AngularVelocityZ = 0,
        Yaw = 0,
        Pitch = 0,
        Roll = 0,

        NormalizedSuspensionTravelFrontLeft = suspensionFrontLeft,
        NormalizedSuspensionTravelFrontRight = 0,
        NormalizedSuspensionTravelRearLeft = 0,
        NormalizedSuspensionTravelRearRight = suspensionRearRight,
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
        TireCombinedSlipFrontLeft = slipFrontLeft,
        TireCombinedSlipFrontRight = 0,
        TireCombinedSlipRearLeft = 0,
        TireCombinedSlipRearRight = 0,
        SuspensionTravelMetersFrontLeft = 0,
        SuspensionTravelMetersFrontRight = 0,
        SuspensionTravelMetersRearLeft = 0,
        SuspensionTravelMetersRearRight = 0,

        CarOrdinal = carOrdinal,
        CarClass = carClass,
        CarPerformanceIndex = performanceIndex,
        DrivetrainType = drivetrain,
        NumCylinders = 4,

        CarGroup = 0,
        SmashableVelDiff = 0,
        SmashableMass = 0,

        PositionX = 0,
        PositionY = 0,
        PositionZ = 0,
        Speed = speedMetersPerSecond,
        Power = power,
        Torque = torque,

        TireTempFrontLeft = tempFrontLeft,
        TireTempFrontRight = tempFrontRight,
        TireTempRearLeft = tempRearLeft,
        TireTempRearRight = tempRearRight,

        Boost = boost,
        Fuel = fuel,

        DistanceTraveled = 0,
        BestLap = 0,
        LastLap = 0,
        CurrentLap = 0,
        CurrentRaceTime = 0,
        LapNumber = 0,
        RacePosition = 0,

        Accel = accel,
        Brake = brake,
        Clutch = clutch,
        HandBrake = handBrake,
        Gear = gear,
        Steer = steer,
        NormalizedDrivingLine = 0,
        NormalizedAIBrakeDifference = 0
    };
}
