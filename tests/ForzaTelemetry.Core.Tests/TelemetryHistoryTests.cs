using ForzaTelemetry.Core.Insights;

namespace ForzaTelemetry.Core.Tests;

public class TelemetryHistoryTests
{
    private static TelemetrySample At(uint ms) => new() { TimeMs = ms };

    [Fact]
    public void Rejects_a_capacity_below_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TelemetryHistory(0));
    }

    [Fact]
    public void Returns_samples_oldest_first_before_the_buffer_is_full()
    {
        var history = new TelemetryHistory(5);
        history.Add(At(1));
        history.Add(At(2));
        history.Add(At(3));

        Assert.Equal([1u, 2u, 3u], history.ToArray().Select(s => s.TimeMs));
        Assert.Equal(3, history.Count);
    }

    [Fact]
    public void Overwrites_the_oldest_samples_once_full_and_keeps_chronological_order()
    {
        var history = new TelemetryHistory(4);
        for (uint i = 1; i <= 10; i++)
        {
            history.Add(At(i));
        }

        Assert.Equal(4, history.Count);
        Assert.Equal([7u, 8u, 9u, 10u], history.ToArray().Select(s => s.TimeMs));
    }

    [Fact]
    public void CopyTo_a_smaller_buffer_returns_only_the_newest_samples_in_order()
    {
        var history = new TelemetryHistory(4);
        for (uint i = 1; i <= 6; i++)
        {
            history.Add(At(i)); // wrapped: holds 3,4,5,6
        }

        var buffer = new TelemetrySample[2];
        var copied = history.CopyTo(buffer);

        Assert.Equal(2, copied);
        Assert.Equal(5u, buffer[0].TimeMs);
        Assert.Equal(6u, buffer[1].TimeMs);
    }

    [Fact]
    public void CopyTo_a_larger_buffer_reports_how_many_were_written()
    {
        var history = new TelemetryHistory(8);
        history.Add(At(1));

        var buffer = new TelemetrySample[5];

        Assert.Equal(1, history.CopyTo(buffer));
    }

    [Fact]
    public void OnPacket_stores_a_normalized_sample_only_while_the_race_is_on()
    {
        var history = new TelemetryHistory(4);
        var packet = TestPackets.Drive(500, 30f, 0f, gear: 3) with
        {
            Accel = 255,
            Brake = 51,
            Steer = -127,
            CurrentEngineRpm = 6000f,
            EngineMaxRpm = 8000f,
            PositionX = 10f,
            PositionZ = -20f,
            TireSlipAngleRearRight = 0.4f,
            NormalizedSuspensionTravelFrontLeft = 0.7f,
            TireTempRearLeft = 180f,
        };

        history.OnPacket(packet);
        history.OnPacket(packet with { IsRaceOn = false });

        var sample = Assert.Single(history.ToArray());
        Assert.Equal(500u, sample.TimeMs);
        Assert.Equal(30f, sample.SpeedMs);
        Assert.Equal(1f, sample.Throttle);
        Assert.Equal(0.2f, sample.Brake, 3);
        Assert.Equal(-1f, sample.Steer);
        Assert.Equal(3, sample.Gear);
        Assert.Equal(6000f, sample.Rpm);
        Assert.Equal(8000f, sample.MaxRpm);
        Assert.Equal(10f, sample.PositionX);
        Assert.Equal(-20f, sample.PositionZ);
        Assert.Equal(0.4f, sample.SlipAngleRearRight);
        Assert.Equal(0.7f, sample.SuspensionFrontLeft);
        Assert.Equal(180f, sample.TireTempRearLeft);
    }

    [Fact]
    public void Steer_is_clamped_to_the_unit_range()
    {
        // -128 is a valid sbyte but outside the documented -127..127 range.
        var sample = TelemetrySample.FromPacket(TestPackets.Drive(0, 0f, 0f) with { Steer = -128 });

        Assert.Equal(-1f, sample.Steer);
    }

    [Fact]
    public void Clear_empties_the_buffer()
    {
        var history = new TelemetryHistory(3);
        history.Add(At(1));

        history.Clear();

        Assert.Equal(0, history.Count);
        Assert.Empty(history.ToArray());
    }

    [Fact]
    public async Task Concurrent_writer_and_reader_never_see_torn_or_misordered_samples()
    {
        // Each sample carries the same value in two fields; a torn read would show them differing.
        var history = new TelemetryHistory(64);
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            for (uint i = 1; i < 200_000; i++)
            {
                history.Add(new TelemetrySample { TimeMs = i, PositionX = i });
            }
        });

        var buffer = new TelemetrySample[64];
        while (!writer.IsCompleted)
        {
            var n = history.CopyTo(buffer);
            for (var i = 0; i < n; i++)
            {
                Assert.Equal((float)buffer[i].TimeMs, buffer[i].PositionX);
                if (i > 0)
                {
                    Assert.Equal(buffer[i - 1].TimeMs + 1, buffer[i].TimeMs);
                }
            }
        }

        await writer;
    }
}
