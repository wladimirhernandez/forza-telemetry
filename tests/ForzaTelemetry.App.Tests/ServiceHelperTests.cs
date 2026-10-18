using System.Globalization;
using System.IO;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using ForzaTelemetry.Core.Laps;

namespace ForzaTelemetry.App.Tests;

/// <summary>Small shared helpers: the system-culture capture, the corrupt-file copy and the engines' observer list.</summary>
public sealed class ServiceHelperTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ForzaTelemetryTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // ---- LanguageService: the system culture is the one from BEFORE the language was applied ----------------------

    [Fact]
    public void The_first_capture_wins_and_a_later_one_cannot_overwrite_it()
    {
        CultureInfo? slot = null;
        var windows = CultureInfo.GetCultureInfo("de-DE");
        var forced = CultureInfo.GetCultureInfo("es");

        var first = LanguageService.CaptureOnce(ref slot, windows);
        var second = LanguageService.CaptureOnce(ref slot, forced); // after Apply changed the thread's culture

        Assert.Same(windows, first);
        Assert.Same(windows, second);
        Assert.Same(windows, slot);
    }

    [Fact]
    public void Apply_does_not_change_what_System_means()
    {
        // Whatever the first capture of this process was (any test may have been first), forcing a language afterwards must
        // never change it: that is exactly the bug the explicit capture fixes.
        var original = CultureInfo.CurrentUICulture;
        var defaultThread = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            var before = LanguageService.CaptureSystemCulture();

            LanguageService.Apply(AppLanguage.Spanish);
            LanguageService.Apply(AppLanguage.English);

            Assert.Same(before, LanguageService.SystemCulture);
            Assert.Same(before, LanguageService.Resolve(AppLanguage.System));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
            CultureInfo.DefaultThreadCurrentUICulture = defaultThread;
        }
    }

    // ---- AtomicFile.KeepCorruptCopy ------------------------------------------------------------------------------

    [Fact]
    public void KeepCorruptCopy_copies_the_file_next_to_the_original_and_overwrites_an_older_copy()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "data.json");
        File.WriteAllText(path, "first");
        Assert.True(AtomicFile.KeepCorruptCopy(path));
        File.WriteAllText(path, "second");

        Assert.True(AtomicFile.KeepCorruptCopy(path));

        Assert.Equal("second", File.ReadAllText(path + ".corrupt"));
        Assert.Equal("second", File.ReadAllText(path)); // the original is untouched
    }

    [Fact]
    public void KeepCorruptCopy_of_a_missing_file_fails_quietly()
    {
        Assert.False(AtomicFile.KeepCorruptCopy(Path.Combine(_directory, "missing.json")));
    }

    // ---- TelemetryEngines -----------------------------------------------------------------------------------------

    [Fact]
    public void Every_engine_is_registered_exactly_once_and_the_car_detector_runs_first()
    {
        var engines = new TelemetryEngines();

        Assert.Same(engines.CarChanges, engines.Observers[0]);
        Assert.Contains(engines.Acceleration, engines.Observers);
        Assert.Contains(engines.Braking, engines.Observers);
        Assert.Contains(engines.TopSpeed, engines.Observers);
        Assert.Contains(engines.Laps, engines.Observers);
        Assert.Equal(engines.Observers.Count, engines.Observers.Distinct().Count());
    }

    [Fact]
    public async Task Packets_sent_through_the_service_reach_every_engine()
    {
        var engines = new TelemetryEngines();
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake, observers: engines.Observers);
        await service.StartAsync();

        fake.Publish(TestPackets.Sample(speedMetersPerSecond: 50, carOrdinal: 5, performanceIndex: 600) with { CurrentLap = 3f });
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));

        Assert.Equal(180, engines.TopSpeed.TopSpeedKmh, precision: 3);
        Assert.Equal(3, engines.Laps.Current.CurrentLapSeconds, precision: 3);
        Assert.True(engines.CarChanges.TryTake(out var change));
        Assert.Equal(new CarKey(5, 600), change!.Car);
        await service.StopAsync();
    }

    // A hard stop from 40 m/s at 10 m/s^2, as packets: 100 km/h to a standstill is 38.58 m.
    private static IEnumerable<TelemetryPacket> HardStop(int carOrdinal)
    {
        uint ms = 1_000;
        double distance = 0;
        TelemetryPacket At(double speed, double d, byte brake) =>
            TestPackets.Sample(speedMetersPerSecond: (float)speed, gear: 5, carOrdinal: carOrdinal, performanceIndex: 700, brake: brake)
            with
            {
                TimestampMS = ms,
                DistanceTraveled = (float)d
            };

        for (var i = 0; i < 5; i++)
        {
            yield return At(40, distance, 0);
            ms += 20;
            distance += 0.8;
        }

        for (var t = 0.02; 40 - (10 * t) > 0; t += 0.02)
        {
            yield return At(40 - (10 * t), distance + (40 * t) - (5 * t * t), 255);
            ms += 20;
        }

        yield return At(0, distance + 80, 255);
    }

    [Fact]
    public void The_braking_test_sees_every_packet_and_follows_the_session_and_car_resets()
    {
        var engines = new TelemetryEngines();
        foreach (var packet in HardStop(5))
        {
            foreach (var observer in engines.Observers)
            {
                observer.OnPacket(packet);
            }
        }

        Assert.Equal(38.58, engines.Braking.Current.HundredToZeroMeters!.Value, precision: 2);

        engines.BeginSession(); // a fresh session forgets the stop
        Assert.Null(engines.Braking.Current.HundredToZeroMeters);

        foreach (var packet in HardStop(5))
        {
            foreach (var observer in engines.Observers)
            {
                observer.OnPacket(packet);
            }
        }

        Assert.NotNull(engines.Braking.Current.HundredToZeroMeters);

        // Another car: the detector resets the braking test on the first packet of the new car, like the other engines.
        foreach (var observer in engines.Observers)
        {
            observer.OnPacket(TestPackets.Sample(speedMetersPerSecond: 10, gear: 3, carOrdinal: 6, performanceIndex: 650));
        }

        Assert.Null(engines.Braking.Current.HundredToZeroMeters);
    }

    [Fact]
    public void A_requested_engine_reset_also_clears_the_braking_test()
    {
        var engines = new TelemetryEngines();
        foreach (var packet in HardStop(5))
        {
            foreach (var observer in engines.Observers)
            {
                observer.OnPacket(packet);
            }
        }

        engines.CarChanges.RequestEngineReset();
        foreach (var observer in engines.Observers)
        {
            observer.OnPacket(TestPackets.Sample(speedMetersPerSecond: 0, gear: 1, carOrdinal: 5, performanceIndex: 700));
        }

        Assert.Null(engines.Braking.Current.HundredToZeroMeters);
    }
}
