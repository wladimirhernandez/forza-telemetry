using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;

namespace ForzaTelemetry.App.Tests;

/// <summary>Packet to view-model mappings behind the shift lights, G-force meter, inputs, engine gauges and car pill.</summary>
public class DashboardViewModelTests
{
    private static async Task<(MainViewModel Vm, FakeTelemetrySource Source, TelemetryService Service)> StartedAsync()
    {
        var source = new FakeTelemetrySource();
        var service = new TelemetryService(() => source);
        var vm = new MainViewModel(service, TimeProvider.System);
        await vm.StartCommand.ExecuteAsync(null);
        return (vm, source, service);
    }

    private static async Task PublishAndRefreshAsync(
        MainViewModel vm, FakeTelemetrySource source, TelemetryService service, TelemetryPacket packet)
    {
        var before = service.ReceivedCount;
        source.Publish(packet);
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == before + 1));
        vm.Refresh();
    }

    [Fact]
    public async Task IsRedline_turns_on_at_97_percent_of_the_redline()
    {
        var (vm, source, service) = await StartedAsync();

        await PublishAndRefreshAsync(vm, source, service, TestPackets.Sample(currentEngineRpm: 7_000f, engineMaxRpm: 8_000f));
        Assert.False(vm.IsRedline);

        await PublishAndRefreshAsync(vm, source, service, TestPackets.Sample(currentEngineRpm: 7_800f, engineMaxRpm: 8_000f));
        Assert.True(vm.IsRedline);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Acceleration_is_converted_from_ms2_to_g_with_the_packet_axes()
    {
        var (vm, source, service) = await StartedAsync();

        // X = right, Z = forward: a hard right turn while braking.
        await PublishAndRefreshAsync(vm, source, service,
            TestPackets.Sample(accelerationX: 9.80665f, accelerationZ: -4.903325f));

        Assert.Equal(1.0, vm.LateralG, precision: 4);
        Assert.Equal(-0.5, vm.LongitudinalG, precision: 4);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Inputs_are_normalized()
    {
        var (vm, source, service) = await StartedAsync();

        await PublishAndRefreshAsync(vm, source, service,
            TestPackets.Sample(accel: 255, brake: 0, clutch: 51, handBrake: 255, steer: -127));

        Assert.Equal(1.0, vm.ThrottleInput, precision: 6);
        Assert.Equal(0.0, vm.BrakeInput, precision: 6);
        Assert.Equal(0.2, vm.ClutchInput, precision: 6);
        Assert.Equal(1.0, vm.HandBrakeInput, precision: 6);
        Assert.Equal(-1.0, vm.SteerInput, precision: 6);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Engine_values_are_converted_and_negatives_are_clamped_to_zero()
    {
        var (vm, source, service) = await StartedAsync();

        await PublishAndRefreshAsync(vm, source, service,
            TestPackets.Sample(power: 200_000f, torque: 350f, boost: 12.5f, fuel: 0.25f));

        Assert.Equal(200.0, vm.PowerKw, precision: 3);
        Assert.Equal(268.204, vm.PowerHp, precision: 2);
        Assert.Equal(350.0, vm.TorqueNm, precision: 3);
        Assert.Equal(12.5, vm.BoostPsi, precision: 3);
        Assert.Equal(25.0, vm.FuelPercent, precision: 3);
        Assert.Equal(0.25, vm.FuelRatio, precision: 6);

        // Off-throttle: engine braking reports negative power/torque, vacuum reports negative boost.
        await PublishAndRefreshAsync(vm, source, service,
            TestPackets.Sample(power: -30_000f, torque: -40f, boost: -6f));

        Assert.Equal(0, vm.PowerKw);
        Assert.Equal(0, vm.TorqueNm);
        Assert.Equal(0, vm.BoostPsi);
        Assert.Equal(0, vm.PowerRatio);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Gauge_ratios_use_the_peak_of_the_current_car_and_restart_for_a_new_car()
    {
        var (vm, source, service) = await StartedAsync();

        await PublishAndRefreshAsync(vm, source, service, TestPackets.Sample(power: 400_000f, carOrdinal: 1));
        Assert.Equal(1.0, vm.PowerRatio, precision: 6); // the new peak
        await PublishAndRefreshAsync(vm, source, service, TestPackets.Sample(power: 200_000f, carOrdinal: 1));
        Assert.Equal(0.5, vm.PowerRatio, precision: 6); // half of that car's 400 kW peak

        // A different car restarts the scale: 200 kW is now the best seen, not half of the old car's peak.
        await PublishAndRefreshAsync(vm, source, service, TestPackets.Sample(power: 200_000f, carOrdinal: 2));
        Assert.Equal(1.0, vm.PowerRatio, precision: 6);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Gauge_ratios_never_use_a_scale_below_the_floor()
    {
        var (vm, source, service) = await StartedAsync();

        await PublishAndRefreshAsync(vm, source, service, TestPackets.Sample(power: 25_000f, torque: 25f, boost: 2.5f));

        Assert.Equal(0.25, vm.PowerRatio, precision: 6); // 25 kW of the 100 kW floor
        Assert.Equal(0.25, vm.TorqueRatio, precision: 6);
        Assert.Equal(0.25, vm.BoostRatio, precision: 6); // 2.5 psi of the 10 psi floor

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Theory]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(999, true)]
    [InlineData(1000, false)]
    public async Task Car_info_requires_a_performance_index_in_100_to_999(int performanceIndex, bool expected)
    {
        var (vm, source, service) = await StartedAsync();

        await PublishAndRefreshAsync(vm, source, service, TestPackets.Sample(performanceIndex: performanceIndex));

        Assert.Equal(expected, vm.HasCarInfo);
        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Car_info_is_hidden_until_a_packet_has_a_performance_index()
    {
        var (vm, source, service) = await StartedAsync();
        Assert.False(vm.HasCarInfo);

        await PublishAndRefreshAsync(vm, source, service, TestPackets.Sample(performanceIndex: 0));
        Assert.False(vm.HasCarInfo);

        await PublishAndRefreshAsync(vm, source, service,
            TestPackets.Sample(carClass: 3, performanceIndex: 780, drivetrain: 2));
        Assert.True(vm.HasCarInfo);
        Assert.Equal("A", vm.CarClassLabel);
        Assert.Equal(780, vm.CarPerformanceIndex);
        Assert.Equal("AWD", vm.DrivetrainLabel);

        await vm.StopCommand.ExecuteAsync(null);
    }
}
