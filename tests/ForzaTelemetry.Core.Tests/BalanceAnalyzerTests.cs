using ForzaTelemetry.Core.Insights;

namespace ForzaTelemetry.Core.Tests;

public class BalanceAnalyzerTests
{
    // ---- Understeer / oversteer ----

    [Fact]
    public void Front_sliding_more_than_rear_is_understeer()
    {
        var reading = BalanceAnalyzer.AnalyzeBalance(0.8f, 0.8f, 0.3f, 0.3f);

        Assert.Equal(BalanceState.Understeer, reading.State);
        Assert.Equal(0.4545f, reading.Index, 2); // (0.8 - 0.3) / 1.1
    }

    [Fact]
    public void Rear_sliding_more_than_front_is_oversteer()
    {
        var reading = BalanceAnalyzer.AnalyzeBalance(0.2f, 0.2f, 0.9f, 0.9f);

        Assert.Equal(BalanceState.Oversteer, reading.State);
        Assert.True(reading.Index < 0);
    }

    [Fact]
    public void Similar_front_and_rear_slip_is_neutral()
    {
        var reading = BalanceAnalyzer.AnalyzeBalance(0.5f, 0.5f, 0.45f, 0.45f);

        Assert.Equal(BalanceState.Neutral, reading.State);
    }

    [Fact]
    public void Opposite_signs_on_left_and_right_tires_do_not_cancel()
    {
        // In a corner the inside and outside tires have opposite slip signs; only magnitude matters.
        var reading = BalanceAnalyzer.AnalyzeBalance(0.8f, -0.8f, 0.2f, -0.2f);

        Assert.Equal(BalanceState.Understeer, reading.State);
    }

    [Fact]
    public void Too_little_slip_cannot_be_judged()
    {
        var reading = BalanceAnalyzer.AnalyzeBalance(0.03f, 0.03f, 0.01f, 0.01f);

        Assert.Equal(BalanceState.Unknown, reading.State);
        Assert.Equal(0f, reading.Index);
    }

    [Fact]
    public void Non_finite_slip_is_unknown()
    {
        Assert.Equal(BalanceState.Unknown, BalanceAnalyzer.AnalyzeBalance(float.NaN, 0.5f, 0.5f, 0.5f).State);
    }

    [Fact]
    public void Packet_and_sample_overloads_read_the_slip_angles()
    {
        var packet = TestPackets.Blank with
        {
            TireSlipAngleFrontLeft = 0.9f,
            TireSlipAngleFrontRight = 0.9f,
            TireSlipAngleRearLeft = 0.1f,
            TireSlipAngleRearRight = 0.1f,
        };

        Assert.Equal(BalanceState.Understeer, BalanceAnalyzer.AnalyzeBalance(packet).State);
        Assert.Equal(BalanceState.Understeer, BalanceAnalyzer.AnalyzeBalance(TelemetrySample.FromPacket(packet)).State);
    }

    // ---- Bottoming ----

    [Theory]
    [InlineData(0.97f, false)]
    [InlineData(0.98f, true)]
    [InlineData(1.0f, true)]
    public void Bottoming_threshold(float travel, bool expected)
    {
        Assert.Equal(expected, BalanceAnalyzer.IsBottomedOut(travel));
    }

    private static TelemetryPacket Susp(float fl = 0, float fr = 0, float rl = 0, float rr = 0) =>
        TestPackets.Blank with
        {
            IsRaceOn = true,
            NormalizedSuspensionTravelFrontLeft = fl,
            NormalizedSuspensionTravelFrontRight = fr,
            NormalizedSuspensionTravelRearLeft = rl,
            NormalizedSuspensionTravelRearRight = rr,
        };

    [Fact]
    public void One_long_impact_counts_once_per_corner()
    {
        var counter = new SuspensionBottomingCounter();

        counter.OnPacket(Susp(fl: 0.5f));
        counter.OnPacket(Susp(fl: 0.99f));
        counter.OnPacket(Susp(fl: 1.0f));
        counter.OnPacket(Susp(fl: 0.985f));

        Assert.Equal(new BottomingCounts(1, 0, 0, 0), counter.Counts);
    }

    [Fact]
    public void Hysteresis_prevents_double_counting_near_the_threshold()
    {
        var counter = new SuspensionBottomingCounter();

        counter.OnPacket(Susp(rr: 0.99f));
        counter.OnPacket(Susp(rr: 0.95f)); // not below the 0.90 re-arm level
        counter.OnPacket(Susp(rr: 0.99f));

        Assert.Equal(1, counter.Counts.RearRight);
    }

    [Fact]
    public void Counts_a_second_event_after_the_corner_decompresses()
    {
        var counter = new SuspensionBottomingCounter();

        counter.OnPacket(Susp(fr: 0.99f));
        counter.OnPacket(Susp(fr: 0.5f));
        counter.OnPacket(Susp(fr: 0.99f));

        Assert.Equal(2, counter.Counts.FrontRight);
    }

    [Fact]
    public void Corners_are_counted_independently_and_total_adds_up()
    {
        var counter = new SuspensionBottomingCounter();

        counter.OnPacket(Susp(fl: 1f, rl: 1f));

        var counts = counter.Counts;
        Assert.Equal(1, counts.FrontLeft);
        Assert.Equal(1, counts.RearLeft);
        Assert.Equal(0, counts.FrontRight);
        Assert.Equal(2, counts.Total);
    }

    [Fact]
    public void Bottoming_ignores_packets_with_race_off_and_reset_clears()
    {
        var counter = new SuspensionBottomingCounter();
        counter.OnPacket(Susp(fl: 1f) with { IsRaceOn = false });
        Assert.Equal(0, counter.Counts.Total);

        counter.OnPacket(Susp(fl: 1f));
        counter.Reset();

        Assert.Equal(0, counter.Counts.Total);
    }

    // ---- Tire temperatures ----

    [Fact]
    public void Balanced_working_temperatures_need_no_advice()
    {
        Assert.Equal(TireAdvice.None, BalanceAnalyzer.AnalyzeTireTemperatures(180f, 180f, 180f, 180f));
    }

    [Fact]
    public void Hot_front_axle_is_flagged()
    {
        var advice = BalanceAnalyzer.AnalyzeTireTemperatures(200f, 200f, 170f, 170f);

        Assert.Equal(TireAdvice.FrontHotterThanRear, advice);
    }

    [Fact]
    public void Hot_rear_axle_is_flagged()
    {
        var advice = BalanceAnalyzer.AnalyzeTireTemperatures(170f, 170f, 200f, 200f);

        Assert.Equal(TireAdvice.RearHotterThanFront, advice);
    }

    [Fact]
    public void Left_and_right_imbalance_are_flagged_and_can_combine_with_axle_advice()
    {
        // Left side hotter (205 vs 175 average) and fronts hotter than rears (200 vs 180): two flags at once.
        var advice = BalanceAnalyzer.AnalyzeTireTemperatures(215f, 185f, 195f, 165f);

        Assert.Equal(TireAdvice.FrontHotterThanRear | TireAdvice.LeftHotterThanRight, advice);
    }

    [Fact]
    public void Right_side_hotter_is_flagged()
    {
        var advice = BalanceAnalyzer.AnalyzeTireTemperatures(170f, 200f, 170f, 200f);

        Assert.Equal(TireAdvice.RightHotterThanLeft, advice);
    }

    [Fact]
    public void Differences_below_the_spread_threshold_are_ignored()
    {
        Assert.Equal(TireAdvice.None, BalanceAnalyzer.AnalyzeTireTemperatures(185f, 185f, 175f, 175f));
    }

    [Fact]
    public void Uniformly_cold_and_uniformly_hot_sets_are_flagged()
    {
        Assert.Equal(TireAdvice.AllTooCold, BalanceAnalyzer.AnalyzeTireTemperatures(100f, 100f, 100f, 100f));
        Assert.Equal(TireAdvice.AllTooHot, BalanceAnalyzer.AnalyzeTireTemperatures(250f, 250f, 250f, 250f));
    }

    [Fact]
    public void Non_finite_temperatures_give_no_advice()
    {
        Assert.Equal(TireAdvice.None, BalanceAnalyzer.AnalyzeTireTemperatures(float.NaN, 180f, 180f, 180f));
    }

    [Fact]
    public void Packet_overload_reads_the_tire_temperatures()
    {
        var packet = TestPackets.Blank with
        {
            TireTempFrontLeft = 200f,
            TireTempFrontRight = 200f,
            TireTempRearLeft = 170f,
            TireTempRearRight = 170f,
        };

        Assert.Equal(TireAdvice.FrontHotterThanRear, BalanceAnalyzer.AnalyzeTireTemperatures(packet));
    }
}
