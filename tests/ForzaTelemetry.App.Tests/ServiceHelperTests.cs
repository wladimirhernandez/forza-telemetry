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
}
