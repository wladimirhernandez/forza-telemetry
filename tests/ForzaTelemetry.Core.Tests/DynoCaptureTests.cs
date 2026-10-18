using ForzaTelemetry.Core.Insights;

namespace ForzaTelemetry.Core.Tests;

public class DynoCaptureTests
{
    private static TelemetryPacket Pull(float rpm, float power, float torque, int car = 1) =>
        TestPackets.Drive(0, 30f, 0f, gear: 3) with
        {
            CarOrdinal = car,
            CurrentEngineRpm = rpm,
            Power = power,
            Torque = torque,
            Accel = 255,
        };

    [Fact]
    public void Bins_by_100_rpm_and_reports_the_bin_center()
    {
        var dyno = new DynoCapture();

        dyno.OnPacket(Pull(4130f, 200_000f, 400f));

        var point = Assert.Single(dyno.GetCurve(1));
        Assert.Equal(4150f, point.Rpm);
        Assert.Equal(200_000f, point.PowerWatts);
        Assert.Equal(400f, point.TorqueNewtonMeters);
    }

    [Fact]
    public void Keeps_the_maximum_per_bin()
    {
        var dyno = new DynoCapture();

        dyno.OnPacket(Pull(4110f, 150_000f, 380f));
        dyno.OnPacket(Pull(4190f, 190_000f, 360f));
        dyno.OnPacket(Pull(4150f, 170_000f, 395f));

        var point = Assert.Single(dyno.GetCurve(1));
        Assert.Equal(190_000f, point.PowerWatts);
        Assert.Equal(395f, point.TorqueNewtonMeters);
    }

    [Fact]
    public void Curve_is_sorted_by_rpm_regardless_of_capture_order()
    {
        var dyno = new DynoCapture();

        dyno.OnPacket(Pull(6000f, 1f, 1f));
        dyno.OnPacket(Pull(3000f, 1f, 1f));
        dyno.OnPacket(Pull(4500f, 1f, 1f));

        Assert.Equal([3050f, 4550f, 6050f], dyno.GetCurve(1).Select(p => p.Rpm));
    }

    [Theory]
    [InlineData("race off")]
    [InlineData("part throttle")]
    [InlineData("clutch in")]
    [InlineData("handbrake")]
    [InlineData("neutral or reverse")]
    [InlineData("no power")]
    [InlineData("nan rpm")]
    public void Rejects_samples_that_are_not_full_load(string reason)
    {
        var dyno = new DynoCapture();
        var packet = Pull(5000f, 100_000f, 300f);
        packet = reason switch
        {
            "race off" => packet with { IsRaceOn = false },
            "part throttle" => packet with { Accel = 241 },
            "clutch in" => packet with { Clutch = 1 },
            "handbrake" => packet with { HandBrake = 1 },
            "neutral or reverse" => packet with { Gear = 0 },
            "no power" => packet with { Power = 0 },
            _ => packet with { CurrentEngineRpm = float.NaN },
        };

        dyno.OnPacket(packet);

        Assert.Empty(dyno.GetCurve(1));
    }

    [Fact]
    public void Accepts_the_throttle_boundary_of_95_percent()
    {
        var dyno = new DynoCapture();

        dyno.OnPacket(Pull(5000f, 100_000f, 300f) with { Accel = 242 });

        Assert.Single(dyno.GetCurve(1));
    }

    [Fact]
    public void Keeps_a_separate_curve_per_car_and_tracks_the_current_one()
    {
        var dyno = new DynoCapture();

        dyno.OnPacket(Pull(5000f, 100_000f, 300f, car: 1));
        dyno.OnPacket(Pull(5000f, 250_000f, 500f, car: 2));

        Assert.Equal(100_000f, dyno.GetCurve(1)[0].PowerWatts);
        Assert.Equal(250_000f, dyno.GetCurve(2)[0].PowerWatts);
        Assert.Equal(2, dyno.CurrentCarOrdinal);
        Assert.Equal(250_000f, dyno.GetCurrentCurve()[0].PowerWatts);
        Assert.Empty(dyno.GetCurve(3));
    }

    [Fact]
    public void Reset_clears_one_car_and_ResetAll_clears_everything()
    {
        var dyno = new DynoCapture();
        dyno.OnPacket(Pull(5000f, 1f, 1f, car: 1));
        dyno.OnPacket(Pull(5000f, 1f, 1f, car: 2));

        dyno.Reset(1);

        Assert.Empty(dyno.GetCurve(1));
        Assert.Single(dyno.GetCurve(2));

        dyno.ResetAll();
        Assert.Empty(dyno.GetCurve(2));
    }

    [Fact]
    public void Ignores_absurd_rpm_beyond_the_bin_range()
    {
        var dyno = new DynoCapture();

        dyno.OnPacket(Pull(99_999f, 1f, 1f));

        Assert.Empty(dyno.GetCurve(1));
    }
}
