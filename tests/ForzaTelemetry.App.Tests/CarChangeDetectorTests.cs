using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using ForzaTelemetry.Core.Performance;

namespace ForzaTelemetry.App.Tests;

/// <summary>The per-packet car-change detector: what it reports, what it ignores, and what it does to the engines it owns.</summary>
public class CarChangeDetectorTests
{
    private static TelemetryPacket Packet(int car, int pi, float speed = 0, int carClass = 3, int drivetrain = 2, bool raceOn = true) =>
        TestPackets.Sample(
            speedMetersPerSecond: speed, carOrdinal: car, performanceIndex: pi, carClass: carClass, drivetrain: drivetrain, isRaceOn: raceOn);

    private static List<CarChange> Drain(CarChangeDetector detector)
    {
        var changes = new List<CarChange>();
        while (detector.TryTake(out var change))
        {
            changes.Add(change!);
        }

        return changes;
    }

    [Fact]
    public void The_first_real_car_is_reported_with_no_previous_car()
    {
        var detector = new CarChangeDetector();

        detector.OnPacket(Packet(1234, 780));
        detector.OnPacket(Packet(1234, 780)); // the same car again: nothing new

        var change = Assert.Single(Drain(detector));
        Assert.Null(change.PreviousCar);
        Assert.Equal(new CarKey(1234, 780), change.Car);
        Assert.Equal(3, change.CarClass);
        Assert.Equal(2, change.Drivetrain);
    }

    [Fact]
    public void A_change_carries_the_old_cars_class_and_drivetrain_and_the_new_ones_separately()
    {
        var detector = new CarChangeDetector();
        detector.OnPacket(Packet(1234, 780, carClass: 3, drivetrain: 2));
        Drain(detector);

        detector.OnPacket(Packet(77, 450, carClass: 1, drivetrain: 0));

        var change = Assert.Single(Drain(detector));
        Assert.Equal(new CarKey(1234, 780), change.PreviousCar);
        Assert.Equal(3, change.PreviousClass);
        Assert.Equal(2, change.PreviousDrivetrain);
        Assert.Equal(new CarKey(77, 450), change.Car);
        Assert.Equal(1, change.CarClass);
        Assert.Equal(0, change.Drivetrain);
    }

    [Fact]
    public void The_same_car_with_a_new_performance_index_is_a_different_build()
    {
        var detector = new CarChangeDetector();
        detector.OnPacket(Packet(1234, 780));
        Drain(detector);

        detector.OnPacket(Packet(1234, 820));

        Assert.Equal(new CarKey(1234, 820), Assert.Single(Drain(detector)).Car);
    }

    [Fact]
    public void Menu_and_loading_packets_neither_start_nor_end_a_car()
    {
        var detector = new CarChangeDetector();
        detector.OnPacket(Packet(1234, 780));
        Drain(detector);

        detector.OnPacket(Packet(0, 0)); // loading: no real car
        detector.OnPacket(Packet(99, 500, raceOn: false)); // a menu preview of another car
        detector.OnPacket(Packet(1234, 780));

        Assert.Empty(Drain(detector));
    }

    [Fact]
    public void A_change_freezes_the_old_top_speed_and_resets_the_engines_before_they_see_the_new_packet()
    {
        var topSpeed = new TopSpeedTracker();
        var detector = new CarChangeDetector(topSpeed);
        var old = Packet(1234, 780, speed: 70);
        detector.OnPacket(old);
        topSpeed.OnPacket(old);

        var next = Packet(77, 450, speed: 20);
        detector.OnPacket(next); // observers run in this order: the detector first ...
        topSpeed.OnPacket(next); // ... then the engine

        var change = Assert.Single(Drain(detector), c => c.PreviousCar is not null);
        Assert.Equal(252, change.PreviousTopSpeedKmh, precision: 3);
        Assert.Equal(72, topSpeed.TopSpeedKmh, precision: 3); // the new car's own maximum, not 252
    }

    [Fact]
    public void The_first_car_of_a_session_does_not_reset_the_engines()
    {
        var topSpeed = new TopSpeedTracker();
        var detector = new CarChangeDetector(topSpeed);
        topSpeed.OnPacket(Packet(0, 0, speed: 40)); // driving before a real car was known (loading with the race on)

        detector.OnPacket(Packet(1234, 780, speed: 41));

        Assert.Equal(144, topSpeed.TopSpeedKmh, precision: 3);
    }

    [Fact]
    public void Reset_forgets_the_car_so_the_same_car_is_reported_again_as_a_first_car()
    {
        var detector = new CarChangeDetector();
        detector.OnPacket(Packet(1234, 780));

        detector.Reset();

        Assert.Empty(Drain(detector)); // unhandled changes of the previous session are dropped
        detector.OnPacket(Packet(1234, 780));
        var change = Assert.Single(Drain(detector));
        Assert.Null(change.PreviousCar);
    }

    [Fact]
    public async Task Changes_made_on_the_receive_thread_are_all_seen_by_the_reader()
    {
        var detector = new CarChangeDetector();

        await Task.Run(() =>
        {
            for (var car = 1; car <= 200; car++)
            {
                detector.OnPacket(Packet(car, 500));
            }
        });

        var changes = Drain(detector);
        Assert.Equal(200, changes.Count);
        Assert.Equal(Enumerable.Range(1, 200), changes.Select(c => c.Car.CarOrdinal));
    }

    [Fact]
    public void Generation_moves_with_every_change_and_with_a_reset_and_pending_changes_are_visible()
    {
        var detector = new CarChangeDetector();
        var start = detector.Generation;
        Assert.False(detector.HasPendingChanges);

        detector.OnPacket(Packet(1234, 780));
        detector.OnPacket(Packet(1234, 780)); // the same car: no change, no new generation

        Assert.Equal(start + 1, detector.Generation);
        Assert.True(detector.HasPendingChanges);

        Drain(detector);
        Assert.False(detector.HasPendingChanges);
        Assert.Equal(start + 1, detector.Generation); // taking a change does not move it

        detector.OnPacket(Packet(77, 450));
        Assert.Equal(start + 2, detector.Generation);

        detector.Reset();
        Assert.Equal(start + 3, detector.Generation);
        Assert.False(detector.HasPendingChanges);
    }

    [Fact]
    public void A_change_of_the_very_first_packet_after_a_reset_is_not_lost()
    {
        // The bug the engines' session reset fixes: a reset that ran AFTER the loop restarted dropped the queued first change.
        // With the reset done first, the first packet of the new session reports its car normally.
        var detector = new CarChangeDetector();
        detector.OnPacket(Packet(1234, 780));
        Drain(detector);
        detector.Reset();

        detector.OnPacket(Packet(77, 450));

        var change = Assert.Single(Drain(detector));
        Assert.Null(change.PreviousCar);
        Assert.Equal(new CarKey(77, 450), change.Car);
    }
}
