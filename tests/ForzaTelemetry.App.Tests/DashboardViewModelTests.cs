using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;

namespace ForzaTelemetry.App.Tests;

/// <summary>Packet to view-model mappings behind the shift lights, G-force meter, inputs, engine gauges and car pill.</summary>
public class DashboardViewModelTests
{
    private static async Task<(ShellViewModel Vm, FakeTelemetrySource Source, TelemetryService Service)> StartedAsync()
    {
        var source = new FakeTelemetrySource();
        var service = new TelemetryService(() => source);
        var vm = new ShellViewModel(service, TimeProvider.System);
        await vm.StartCommand.ExecuteAsync(null);
        return (vm, source, service);
    }

    private static async Task PublishAndRefreshAsync(
        ShellViewModel vm, FakeTelemetrySource source, TelemetryService service, TelemetryPacket packet)
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
        Assert.False(vm.Dashboard.IsRedline);

        await PublishAndRefreshAsync(vm, source, service, TestPackets.Sample(currentEngineRpm: 7_800f, engineMaxRpm: 8_000f));
        Assert.True(vm.Dashboard.IsRedline);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Acceleration_is_converted_from_ms2_to_g_with_the_packet_axes()
    {
        var (vm, source, service) = await StartedAsync();

        // X = right, Z = forward: a hard right turn while braking.
        await PublishAndRefreshAsync(vm, source, service,
            TestPackets.Sample(accelerationX: 9.80665f, accelerationZ: -4.903325f));

        Assert.Equal(1.0, vm.Dashboard.LateralG, precision: 4);
        Assert.Equal(-0.5, vm.Dashboard.LongitudinalG, precision: 4);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Inputs_are_normalized()
    {
        var (vm, source, service) = await StartedAsync();

        await PublishAndRefreshAsync(vm, source, service,
            TestPackets.Sample(accel: 255, brake: 0, clutch: 51, handBrake: 255, steer: -127));

        Assert.Equal(1.0, vm.Dashboard.ThrottleInput, precision: 6);
        Assert.Equal(0.0, vm.Dashboard.BrakeInput, precision: 6);
        Assert.Equal(0.2, vm.Dashboard.ClutchInput, precision: 6);
        Assert.Equal(1.0, vm.Dashboard.HandBrakeInput, precision: 6);
        Assert.Equal(-1.0, vm.Dashboard.SteerInput, precision: 6);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Engine_values_are_converted_and_negatives_are_clamped_to_zero()
    {
        var (vm, source, service) = await StartedAsync();

        await PublishAndRefreshAsync(vm, source, service,
            TestPackets.Sample(power: 200_000f, torque: 350f, boost: 12.5f, fuel: 0.25f));

        Assert.Equal(200.0, vm.Dashboard.PowerKw, precision: 3);
        Assert.Equal(268.204, vm.Dashboard.PowerHp, precision: 2);
        Assert.Equal(350.0, vm.Dashboard.TorqueNm, precision: 3);
        Assert.Equal(12.5, vm.Dashboard.BoostPsi, precision: 3);
        Assert.Equal(25.0, vm.Dashboard.FuelPercent, precision: 3);
        Assert.Equal(0.25, vm.Dashboard.FuelRatio, precision: 6);

        // Off-throttle: engine braking reports negative power/torque, vacuum reports negative boost.
        await PublishAndRefreshAsync(vm, source, service,
            TestPackets.Sample(power: -30_000f, torque: -40f, boost: -6f));

        Assert.Equal(0, vm.Dashboard.PowerKw);
        Assert.Equal(0, vm.Dashboard.TorqueNm);
        Assert.Equal(0, vm.Dashboard.BoostPsi);
        Assert.Equal(0, vm.Dashboard.PowerRatio);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Gauge_ratios_use_the_peak_of_the_current_car_and_restart_for_a_new_car()
    {
        var (vm, source, service) = await StartedAsync();

        await PublishAndRefreshAsync(vm, source, service, TestPackets.Sample(power: 400_000f, carOrdinal: 1));
        Assert.Equal(1.0, vm.Dashboard.PowerRatio, precision: 6); // the new peak
        await PublishAndRefreshAsync(vm, source, service, TestPackets.Sample(power: 200_000f, carOrdinal: 1));
        Assert.Equal(0.5, vm.Dashboard.PowerRatio, precision: 6); // half of that car's 400 kW peak

        // A different car restarts the scale: 200 kW is now the best seen, not half of the old car's peak.
        await PublishAndRefreshAsync(vm, source, service, TestPackets.Sample(power: 200_000f, carOrdinal: 2));
        Assert.Equal(1.0, vm.Dashboard.PowerRatio, precision: 6);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Gauge_ratios_never_use_a_scale_below_the_floor()
    {
        var (vm, source, service) = await StartedAsync();

        await PublishAndRefreshAsync(vm, source, service, TestPackets.Sample(power: 25_000f, torque: 25f, boost: 2.5f));

        Assert.Equal(0.25, vm.Dashboard.PowerRatio, precision: 6); // 25 kW of the 100 kW floor
        Assert.Equal(0.25, vm.Dashboard.TorqueRatio, precision: 6);
        Assert.Equal(0.25, vm.Dashboard.BoostRatio, precision: 6); // 2.5 psi of the 10 psi floor

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

        Assert.Equal(expected, vm.Dashboard.HasCarInfo);
        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Car_info_is_hidden_until_a_packet_has_a_performance_index()
    {
        var (vm, source, service) = await StartedAsync();
        Assert.False(vm.Dashboard.HasCarInfo);

        await PublishAndRefreshAsync(vm, source, service, TestPackets.Sample(performanceIndex: 0));
        Assert.False(vm.Dashboard.HasCarInfo);

        await PublishAndRefreshAsync(vm, source, service,
            TestPackets.Sample(carClass: 3, performanceIndex: 780, drivetrain: 2));
        Assert.True(vm.Dashboard.HasCarInfo);
        Assert.Equal("A", vm.Dashboard.CarClassLabel);
        Assert.Equal(780, vm.Dashboard.CarPerformanceIndex);
        Assert.Equal("AWD", vm.Dashboard.DrivetrainLabel);

        await vm.StopCommand.ExecuteAsync(null);
    }

    // ---- The car pill's name -------------------------------------------------------------------------------------

    [Fact]
    public void The_car_name_follows_the_car_and_a_rename_and_survives_menu_packets()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ForzaTelemetryTests", Guid.NewGuid().ToString("N"));
        try
        {
            var names = new CarNameStore(System.IO.Path.Combine(directory, "cars.json"));
            names.TrySet(11, "Daily GT");
            var dashboard = new DashboardViewModel(carNames: names);
            TelemetryPacket Drive(int ordinal) => TestPackets.Sample(carOrdinal: ordinal, performanceIndex: 700);

            dashboard.Apply(Drive(11));
            Assert.Equal("Daily GT", dashboard.CarName);

            dashboard.Apply(Drive(11)); // the same car on the next tick keeps the name
            Assert.Equal("Daily GT", dashboard.CarName);

            dashboard.Apply(Drive(22)); // another car, unnamed
            Assert.Equal(CarDisplayName.Unnamed(22), dashboard.CarName);

            dashboard.Apply(Drive(11)); // and back
            Assert.Equal("Daily GT", dashboard.CarName);

            names.TrySet(11, "Track toy"); // a rename of the car on screen is picked up at once
            Assert.Equal("Track toy", dashboard.CarName);

            dashboard.ResetLive(); // pause menu: no car
            Assert.Equal("", dashboard.CarName);
            dashboard.Apply(Drive(11));
            Assert.Equal("Track toy", dashboard.CarName);

            names.TrySet(22, "Rally"); // a rename of a car that is NOT on screen changes nothing here
            Assert.Equal("Track toy", dashboard.CarName);
        }
        finally
        {
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
    }
}
