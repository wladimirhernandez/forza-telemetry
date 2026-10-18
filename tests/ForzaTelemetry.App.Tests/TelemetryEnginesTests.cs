using ForzaTelemetry.Core;

namespace ForzaTelemetry.App.Tests;

/// <summary>The one session reset of the per-packet engines: what it clears, and what it must keep.</summary>
public class TelemetryEnginesTests
{
    private static void Feed(TelemetryEngines engines, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var packet = InsightPackets.Pull(3000 + (i * 400), 150_000 + (i * 5_000), 300, carOrdinal: 2211, performanceIndex: 700) with
            {
                TimestampMS = (uint)(1000 + (i * 16)),
                DistanceTraveled = i * 20f,
                LapNumber = (ushort)(i / 5),
                CurrentLap = 1f + i,
            };
            foreach (var observer in engines.Observers)
            {
                observer.OnPacket(packet);
            }
        }
    }

    [Fact]
    public void BeginSession_empties_every_per_session_engine_and_forgets_the_car()
    {
        var engines = new TelemetryEngines();
        Feed(engines, 12);
        Assert.True(engines.History.Count > 0);
        Assert.True(engines.Path.Count > 0);
        Assert.True(engines.TopSpeed.TopSpeedMs > 0);
        Assert.NotEmpty(engines.Laps.Current.Laps);
        Assert.True(engines.CarChanges.HasPendingChanges);

        engines.BeginSession();

        Assert.Equal(0, engines.History.Count);
        Assert.Equal(0, engines.Path.Count);
        Assert.Equal(0, engines.TopSpeed.TopSpeedMs);
        Assert.Empty(engines.Laps.Current.Laps);
        Assert.Equal(0, engines.Bottoming.Counts.Total);
        Assert.False(engines.CarChanges.HasPendingChanges); // the unhandled change of the old session is dropped
    }

    [Fact]
    public void BeginSession_keeps_the_dyno_curves_because_a_curve_belongs_to_the_car_not_the_session()
    {
        var engines = new TelemetryEngines();
        Feed(engines, 12);
        var points = engines.Dyno.GetCurve(2211, 700).Count;
        Assert.True(points > 0);

        engines.BeginSession();

        Assert.Equal(points, engines.Dyno.GetCurve(2211, 700).Count);
    }

    [Fact]
    public void The_same_car_is_reported_as_a_first_car_again_after_BeginSession()
    {
        var engines = new TelemetryEngines();
        Feed(engines, 3);
        while (engines.CarChanges.TryTake(out _))
        {
        }

        engines.BeginSession();
        Feed(engines, 1);

        Assert.True(engines.CarChanges.TryTake(out var change));
        Assert.Null(change!.PreviousCar);
    }
}
