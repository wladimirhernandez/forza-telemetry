using ForzaTelemetry.Core.Performance;

namespace ForzaTelemetry.Core.Tests;

public class TopSpeedTrackerTests
{
    [Fact]
    public void Keeps_the_maximum_speed()
    {
        var tracker = new TopSpeedTracker();

        tracker.OnPacket(TestPackets.Drive(0, 20f, 0));
        tracker.OnPacket(TestPackets.Drive(20, 80f, 0));
        tracker.OnPacket(TestPackets.Drive(40, 50f, 0));

        Assert.Equal(80, tracker.TopSpeedMs, 3);
        Assert.Equal(288, tracker.TopSpeedKmh, 1);
    }

    [Fact]
    public void Ignores_packets_with_race_off_and_non_finite_speeds()
    {
        var tracker = new TopSpeedTracker();

        tracker.OnPacket(TestPackets.Drive(0, 90f, 0) with { IsRaceOn = false });
        tracker.OnPacket(TestPackets.Drive(20, float.PositiveInfinity, 0));
        tracker.OnPacket(TestPackets.Drive(40, float.NaN, 0));

        Assert.Equal(0, tracker.TopSpeedMs);
    }

    [Fact]
    public void Reset_clears_the_maximum()
    {
        var tracker = new TopSpeedTracker();
        tracker.OnPacket(TestPackets.Drive(0, 60f, 0));

        tracker.Reset();

        Assert.Equal(0, tracker.TopSpeedMs);
        tracker.OnPacket(TestPackets.Drive(20, 10f, 0));
        Assert.Equal(10, tracker.TopSpeedMs, 3);
    }
}
