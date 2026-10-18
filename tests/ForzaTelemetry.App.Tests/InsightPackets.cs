using ForzaTelemetry.Core;

namespace ForzaTelemetry.App.Tests;

/// <summary>
/// Packets for the insights engines (path, dyno, suspension), built on top of <see cref="TestPackets.Sample"/> so a test
/// names only the few fields it cares about.
/// </summary>
internal static class InsightPackets
{
    /// <summary>A driving packet at a world position.</summary>
    public static TelemetryPacket At(float x, float z, float speed = 30, byte brake = 0) =>
        TestPackets.Sample(speedMetersPerSecond: speed, brake: brake) with { PositionX = x, PositionZ = z };

    /// <summary>A full-throttle packet that the dyno capture accepts (third gear, clutch released).</summary>
    public static TelemetryPacket Pull(float rpm, float powerWatts, float torque, int carOrdinal, int performanceIndex) =>
        TestPackets.Sample(
            currentEngineRpm: rpm, power: powerWatts, torque: torque, accel: 255, gear: 3,
            carOrdinal: carOrdinal, performanceIndex: performanceIndex);

    /// <summary>A packet with the given normalized suspension travel on the chosen corners.</summary>
    public static TelemetryPacket Suspension(float frontLeft = 0, float rearRight = 0) =>
        TestPackets.Sample(suspensionFrontLeft: frontLeft, suspensionRearRight: rearRight);
}
