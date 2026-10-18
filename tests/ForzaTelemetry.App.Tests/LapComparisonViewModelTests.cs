using System.Globalization;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using ForzaTelemetry.Core.Insights;
using ForzaTelemetry.Core.Laps;

namespace ForzaTelemetry.App.Tests;

/// <summary>The lap comparison half of the Analysis page: what it copies, when the chart is told to rebuild, and the text it shows.</summary>
public class LapComparisonViewModelTests
{
    private const double Length = 1000; // meters per lap in the synthetic track

    private static TelemetryPacket Packet(int lapsCompleted, double lapTime, double distance, double lastLap = 0) =>
        TestPackets.Sample(speedMetersPerSecond: 40) with
        {
            IsRaceOn = true,
            LapNumber = (ushort)lapsCompleted,
            CurrentLap = (float)lapTime,
            DistanceTraveled = (float)distance,
            LastLap = (float)lastLap,
        };

    /// <summary>One lap at constant speed, 100 ms per packet, up to <paramref name="stopAtSeconds"/>.</summary>
    private static void DriveLap(
        LapTraceRecorder recorder, int lapsCompleted, double lapSeconds, double startDistance, double lastLap = 0,
        double? stopAtSeconds = null, double startAtSeconds = 0)
    {
        var speed = Length / lapSeconds;
        for (var t = startAtSeconds; t <= (stopAtSeconds ?? lapSeconds) + 1e-9; t += 0.1)
        {
            recorder.OnPacket(Packet(lapsCompleted, t, startDistance + (speed * t), lastLap));
        }
    }

    private static (LapComparisonViewModel Vm, LapTraceRecorder Recorder, UnitsService Units) Rig()
    {
        var recorder = new LapTraceRecorder();
        var units = new UnitsService();
        return (new LapComparisonViewModel(recorder, units), recorder, units);
    }

    [Fact]
    public void Starts_empty_with_placeholders()
    {
        var (vm, _, _) = Rig();

        vm.Refresh();

        Assert.False(vm.HasBestLap);
        Assert.True(vm.IsEmpty);
        Assert.Equal(0, vm.CurrentCount);
        Assert.Equal(LapTimeFormatter.Placeholder, vm.BestLapText);
        Assert.Equal(LapTimeFormatter.Placeholder, vm.GapText);
        Assert.Equal(LapDeltaState.Hidden, vm.GapState);
        Assert.False(vm.HasNote);
    }

    [Fact]
    public void A_best_lap_turns_the_empty_state_into_the_chart()
    {
        var (vm, recorder, _) = Rig();
        DriveLap(recorder, 0, 20, 0);
        recorder.OnPacket(Packet(1, 0.0, 1000, lastLap: 20));

        vm.Refresh();

        Assert.True(vm.HasBestLap);
        Assert.False(vm.IsEmpty);
        Assert.Equal(LapTimeFormatter.Format(20), vm.BestLapText);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.LapsLapFormat, 1), vm.BestLapNote);
    }

    [Fact]
    public void The_gap_is_the_current_clock_minus_the_best_lap_clock_at_the_same_distance()
    {
        var (vm, recorder, _) = Rig();
        DriveLap(recorder, 0, 20, 0); // 50 m/s: 500 m at 10.0 s
        DriveLap(recorder, 1, 22, 1000, lastLap: 20, stopAtSeconds: 11.0); // reaches 500 m at 11.0 s

        vm.Refresh();

        Assert.Equal(LapDeltaState.Behind, vm.GapState);
        Assert.Equal(LapTimeFormatter.FormatDelta(1.0), vm.GapText);
        Assert.Equal(LapTimeFormatter.Format(11.0), vm.CurrentLapText);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.LapsLapFormat, 2), vm.CurrentLapNote);
    }

    [Fact]
    public void Being_faster_than_the_best_lap_reads_as_ahead()
    {
        var (vm, recorder, _) = Rig();
        DriveLap(recorder, 0, 20, 0);
        DriveLap(recorder, 1, 18, 1000, lastLap: 20, stopAtSeconds: 9.0);

        vm.Refresh();

        Assert.Equal(LapDeltaState.Ahead, vm.GapState);
        Assert.StartsWith("-", vm.GapText);
    }

    [Fact]
    public void A_lap_joined_midway_is_not_drawn_and_the_page_says_why()
    {
        var (vm, recorder, _) = Rig();
        for (var t = 10.0; t <= 15; t += 0.1)
        {
            recorder.OnPacket(Packet(0, t, 500 + ((t - 10) * 50)));
        }

        vm.Refresh();

        Assert.Equal(0, vm.CurrentCount);
        Assert.True(vm.HasNote);
        Assert.Equal(InsightsStrings.CompareNotComparable, vm.Note);
    }

    [Fact]
    public void The_chart_is_told_to_rebuild_only_when_something_changed()
    {
        var (vm, recorder, _) = Rig();
        DriveLap(recorder, 0, 20, 0);
        recorder.OnPacket(Packet(1, 0.0, 1000, lastLap: 20));
        vm.Refresh();
        var data = vm.DataVersion;
        var best = vm.BestVersion;

        vm.Refresh();
        Assert.Equal(data, vm.DataVersion); // nothing new: the chart must not rebuild

        recorder.OnPacket(Packet(1, 0.5, 1030));
        vm.Refresh();
        Assert.NotEqual(data, vm.DataVersion);
        Assert.Equal(best, vm.BestVersion); // a new sample of the current lap leaves the best lap's lines alone
    }

    [Fact]
    public void A_unit_change_rebuilds_and_relabels()
    {
        var recorder = new LapTraceRecorder();
        var units = new UnitsService();
        var vm = new LapComparisonViewModel(recorder, units);
        DriveLap(recorder, 0, 20, 0);
        recorder.OnPacket(Packet(1, 0.0, 1000, lastLap: 20));
        vm.Refresh();
        var version = vm.DataVersion;

        units.SpeedUnit = SpeedUnit.Mph;
        vm.Refresh();

        Assert.NotEqual(version, vm.DataVersion);
        Assert.Equal(Strings.SpeedUnitMph, vm.SpeedUnitLabel);
        Assert.Equal(UnitsService.KmhToMph(3.6), vm.SpeedScale, 6);
    }

    [Fact]
    public void BeginSession_empties_the_display_and_forces_a_redraw()
    {
        var (vm, recorder, _) = Rig();
        DriveLap(recorder, 0, 20, 0);
        recorder.OnPacket(Packet(1, 0.0, 1000, lastLap: 20));
        vm.Refresh();
        var version = vm.DataVersion;

        recorder.Reset(); // what the engine reset does before the loop starts
        vm.BeginSession();

        Assert.False(vm.HasBestLap);
        Assert.Equal(0, vm.CurrentCount);
        Assert.NotEqual(version, vm.DataVersion);
        Assert.Equal(LapTimeFormatter.Placeholder, vm.BestLapText);
    }

    [Theory]
    [InlineData(1500, SpeedUnit.Kmh, 1, "m")]
    [InlineData(3200, SpeedUnit.Kmh, 0.001, "km")]
    [InlineData(500, SpeedUnit.Mph, UnitsService.FeetPerMeter, "ft")]
    [InlineData(3200, SpeedUnit.Mph, 1 / (UnitsService.KilometersPerMile * 1000), "mi")]
    public void The_distance_axis_unit_follows_the_lap_length_and_the_speed_unit(double meters, SpeedUnit speedUnit, double scale, string label)
    {
        var (actualScale, actualLabel) = LapComparisonViewModel.ChooseDistanceUnit(meters, speedUnit);

        Assert.Equal(scale, actualScale, 9);
        Assert.Equal(label, actualLabel);
    }

    // ---- The page around it -------------------------------------------------------------------------

    [Fact]
    public void The_analysis_page_offers_the_mode_selector_only_with_a_recorder()
    {
        var without = new AnalysisViewModel(new TelemetryHistory(), new UnitsService());
        var with = new AnalysisViewModel(new TelemetryHistory(), new UnitsService(), new LapTraceRecorder());

        Assert.False(without.HasModes);
        Assert.Null(without.Comparison);
        Assert.Empty(without.ModeOptions);
        Assert.True(with.HasModes);
        Assert.Equal([AnalysisMode.Live, AnalysisMode.LapComparison], with.ModeOptions.Select(o => o.Value));
        Assert.True(with.IsLiveMode);
    }

    [Fact]
    public void Only_the_mode_on_screen_is_refreshed()
    {
        var history = new TelemetryHistory();
        history.Add(new TelemetrySample { TimeMs = 10, SpeedMs = 20 });
        var recorder = new LapTraceRecorder();
        var page = new AnalysisViewModel(history, new UnitsService(), recorder);
        DriveLap(recorder, 0, 20, 0);
        recorder.OnPacket(Packet(1, 0.0, 1000, lastLap: 20));

        page.Refresh();
        Assert.True(page.HasData);
        Assert.False(page.Comparison!.HasBestLap); // not refreshed while live traces show

        page.Mode = AnalysisMode.LapComparison;
        page.Refresh();
        Assert.True(page.Comparison.HasBestLap);
    }

    [Fact]
    public void Switching_back_to_live_catches_up_on_the_next_refresh()
    {
        var history = new TelemetryHistory();
        history.Add(new TelemetrySample { TimeMs = 10, SpeedMs = 20 });
        var page = new AnalysisViewModel(history, new UnitsService(), new LapTraceRecorder());
        page.Refresh();
        page.Mode = AnalysisMode.LapComparison;
        page.Refresh();
        history.Add(new TelemetrySample { TimeMs = 30, SpeedMs = 25 });
        var version = page.DataVersion;

        page.Mode = AnalysisMode.Live;
        page.Refresh();

        Assert.NotEqual(version, page.DataVersion);
        Assert.Equal(2, page.SampleCount);
    }

    [Fact]
    public void The_chart_and_empty_flags_follow_the_mode()
    {
        var page = new AnalysisViewModel(new TelemetryHistory(), new UnitsService(), new LapTraceRecorder());
        page.Refresh();

        Assert.True(page.ShowLiveEmpty);
        Assert.False(page.ShowLiveChart);

        page.Mode = AnalysisMode.LapComparison;

        Assert.False(page.ShowLiveEmpty);
        Assert.True(page.IsCompareMode);
    }

    [Fact]
    public void The_lap_in_progress_is_copied_only_from_where_the_last_copy_ended()
    {
        var (vm, recorder, _) = Rig();
        DriveLap(recorder, 0, 20, 0, stopAtSeconds: 3.0);
        vm.Refresh();
        var first = vm.CurrentCount;
        Assert.True(first > 2);

        // Overwrite a sample the view model already copied: a copy from the start would restore it, a tail copy leaves it alone.
        vm.CurrentTrace[0] = new LapTraceSample { DistanceMeters = -1 };
        DriveLap(recorder, 0, 20, 0, stopAtSeconds: 4.0, startAtSeconds: 3.1);
        vm.Refresh();

        Assert.True(vm.CurrentCount > first);
        Assert.Equal(-1, vm.CurrentTrace[0].DistanceMeters);
        Assert.Equal(recorder.Status.CurrentCount, vm.CurrentCount);
        Assert.True(vm.CurrentTrace[vm.CurrentCount - 1].DistanceMeters > vm.CurrentTrace[first - 1].DistanceMeters);
    }

    [Fact]
    public void A_new_lap_is_copied_from_its_first_sample()
    {
        var (vm, recorder, _) = Rig();
        DriveLap(recorder, 0, 20, 0);
        recorder.OnPacket(Packet(1, 0.0, 1000, lastLap: 20));
        DriveLap(recorder, 1, 22, 1000, lastLap: 20, stopAtSeconds: 2.0);
        vm.Refresh();
        vm.CurrentTrace[0] = new LapTraceSample { DistanceMeters = -1 };

        recorder.OnPacket(Packet(2, 0.0, 2000, lastLap: 22)); // lap 2 ended: the lap in progress is a new one
        DriveLap(recorder, 2, 22, 2000, lastLap: 22, stopAtSeconds: 1.0);
        vm.Refresh();

        Assert.Equal(0, vm.CurrentTrace[0].DistanceMeters); // the first sample of the new lap, not the marker left by the old copy
        Assert.Equal(recorder.Status.CurrentCount, vm.CurrentCount);
    }
}
