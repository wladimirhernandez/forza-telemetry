using System.IO;
using System.Globalization;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.App.Tests;

/// <summary>Startup must degrade, never fail, for recoverable reasons: a culture the runtime lacks, a store or adapter that throws.</summary>
public class StartupRobustnessTests
{
    // What GetCultureInfo does in invariant-globalization mode (or without ICU): it throws for any culture but the invariant one.
    private static CultureInfo NoCultures(string name) => throw new CultureNotFoundException(nameof(name), name, "Culture is not supported.");

    private sealed class ThrowingSettingsStore : ISettingsStore
    {
        public AppSettings Load() => throw new UnauthorizedAccessException("blocked by policy");

        public bool TrySave(AppSettings settings) => false;
    }

    private sealed class ThrowingNetworkInfo : INetworkInfo
    {
        public IReadOnlyList<LanAddress> GetLanAddresses() => throw new PlatformNotSupportedException("no adapters API");
    }

    // ---- Culture ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Spanish)]
    public void A_culture_the_runtime_lacks_resolves_to_nothing_and_is_logged(AppLanguage language)
    {
        var logger = new ListLogger<LanguageServiceMarker>();

        var culture = LanguageService.ResolveCulture(language, NoCultures, logger);

        Assert.Null(culture);
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.IsType<CultureNotFoundException>(warning.Exception);
    }

    [Fact]
    public void System_never_asks_the_runtime_for_a_culture()
    {
        var logger = new ListLogger<LanguageServiceMarker>();

        var culture = LanguageService.ResolveCulture(AppLanguage.System, NoCultures, logger);

        Assert.Null(culture);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Apply_with_an_unavailable_culture_keeps_the_current_one_and_does_not_throw()
    {
        var before = CultureInfo.CurrentUICulture;
        var beforeDefault = CultureInfo.DefaultThreadCurrentUICulture;
        var logger = new ListLogger<LanguageServiceMarker>();

        var applied = LanguageService.Apply(AppLanguage.Spanish, NoCultures, logger);

        Assert.Same(before, applied);
        Assert.Same(before, CultureInfo.CurrentUICulture);
        Assert.Same(beforeDefault, CultureInfo.DefaultThreadCurrentUICulture); // nothing leaked to threads created later
        Assert.Single(logger.Entries);
    }

    [Fact]
    public void The_neutral_strings_resolve_without_any_culture_data()
    {
        // The fallback culture in those environments is the invariant one: the lookup must land on the neutral English file.
        Assert.False(string.IsNullOrEmpty(Strings.ResourceManager.GetString(nameof(Strings.StatusStopped), CultureInfo.InvariantCulture)));
        Assert.False(string.IsNullOrEmpty(Strings.ResourceManager.GetString(nameof(Strings.TabDashboard), CultureInfo.InvariantCulture)));
    }

    // ---- Optional startup steps -----------------------------------------------------------------------------

    [Fact]
    public void A_failing_step_is_logged_by_name_and_reported_as_failed()
    {
        var logger = new ListLogger<StartupRobustnessTests>();

        var ok = StartupGuard.Run(logger, "load performance history", () => throw new IOException("disk"));

        Assert.False(ok);
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("load performance history", warning.Message);
        Assert.IsType<IOException>(warning.Exception);
    }

    [Fact]
    public void A_step_that_works_is_not_logged()
    {
        var logger = new ListLogger<StartupRobustnessTests>();
        var ran = false;

        Assert.True(StartupGuard.Run(logger, "step", () => ran = true));

        Assert.True(ran);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void A_settings_store_that_throws_yields_the_defaults_and_a_working_settings_service()
    {
        var logger = new ListLogger<StartupRobustnessTests>();
        var store = new ThrowingSettingsStore();

        var settings = new SettingsService(store, StartupGuard.Run(logger, "load settings", store.Load, AppSettings.Default));

        Assert.Equal(AppSettings.Default, settings.Current);
        Assert.Contains("load settings", Assert.Single(logger.Entries).Message);
        settings.Update(s => s with { UdpPort = 5301 }); // a failing save is the store's business and must not throw either
        Assert.Equal(5301, settings.Current.UdpPort);
    }

    [Fact]
    public async Task Failing_network_enumeration_shows_not_found_and_never_throws()
    {
        var logger = new ListLogger<StartupRobustnessTests>();

        var dataOut = new DataOutViewModel(5300, new ThrowingNetworkInfo(), logger: logger);
        await dataOut.RefreshAsync();

        Assert.Equal(Strings.DataOutIpNone, dataOut.Address);
        Assert.Null(dataOut.PrimaryAddress);
        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Exception is PlatformNotSupportedException));
    }

    /// <summary>Category type for the static LanguageService logger (a static class cannot be a type argument).</summary>
    private sealed class LanguageServiceMarker;

    // ---- Invariant-globalization detection -------------------------------------------------------------------

    private static CultureInfo AnyCulture(string name) => CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(null, "true", true)]
    [InlineData(null, "TRUE", true)]
    [InlineData(null, " 1 ", true)]
    [InlineData(false, "true", false)] // an explicit runtimeconfig "false" wins: the runtime never reads the variable then
    [InlineData(false, "1", false)]
    [InlineData(null, "false", false)]
    [InlineData(null, "0", false)]
    [InlineData(null, "", false)]
    [InlineData(null, null, false)]
    [InlineData(false, null, false)]
    public void The_switch_and_the_environment_variable_both_mean_no_culture_data(bool? switchValue, string? environment, bool expected) =>
        Assert.Equal(expected, LanguageService.DetectInvariantGlobalization(switchValue, environment, AnyCulture));

    [Fact]
    public void A_failing_culture_probe_means_no_culture_data_even_with_no_switch_or_variable()
    {
        Assert.True(LanguageService.DetectInvariantGlobalization(null, null, NoCultures));
        Assert.True(LanguageService.DetectInvariantGlobalization(
            null, null, _ => throw new InvalidOperationException("platform failure")));
    }

    // ---- Settings that exist but cannot be read ----------------------------------------------------------------

    private sealed class RecordingStore(bool failedToRead, bool throwOnLoad = false) : ISettingsStore
    {
        public int Saves { get; private set; }

        public bool LoadFailedToRead => failedToRead;

        public AppSettings Load() => throwOnLoad ? throw new UnauthorizedAccessException("blocked") : AppSettings.Default;

        public bool TrySave(AppSettings settings)
        {
            Saves++;
            return true;
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_file_that_could_not_be_read_means_defaults_for_the_session_and_nothing_written(bool failedToRead, bool throws)
    {
        var logger = new ListLogger<StartupRobustnessTests>();
        var store = new RecordingStore(failedToRead, throws);

        var settings = SettingsService.LoadFrom(store, logger);
        settings.Update(s => s with { UdpPort = 5301 });

        Assert.True(settings.IsPersistenceSuspended);
        Assert.Equal(5301, settings.Current.UdpPort); // the change applies for this run
        Assert.Equal(0, store.Saves); // and the user's real file is never replaced by it
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Settings_that_loaded_normally_are_saved_as_always()
    {
        var store = new RecordingStore(failedToRead: false);

        var settings = SettingsService.LoadFrom(store, new ListLogger<StartupRobustnessTests>());
        settings.Update(s => s with { UdpPort = 5301 });

        Assert.False(settings.IsPersistenceSuspended);
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public void A_locked_settings_file_survives_the_session_byte_for_byte()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ForzaTelemetryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            const string original = """{ "schemaVersion": 1, "udpPort": 5400, "hasCompletedSetup": true }""";
            File.WriteAllText(path, original);
            var store = new JsonSettingsStore(path);
            SettingsService settings;
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) // another process holds the file
            {
                settings = SettingsService.LoadFrom(store, new ListLogger<StartupRobustnessTests>());
            }

            Assert.True(store.LoadFailedToRead);
            Assert.True(settings.IsPersistenceSuspended);
            Assert.Equal(AppSettings.Default.UdpPort, settings.Current.UdpPort); // a stand-in, not what the file says

            settings.Update(s => s with { SpeedUnit = SpeedUnit.Mph });

            Assert.Equal(original, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_missing_settings_file_is_a_normal_first_run_that_can_be_saved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ForzaTelemetryTests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new JsonSettingsStore(path);

            var settings = SettingsService.LoadFrom(store, new ListLogger<StartupRobustnessTests>());
            settings.Update(s => s with { SpeedUnit = SpeedUnit.Mph });

            Assert.False(store.LoadFailedToRead);
            Assert.False(settings.IsPersistenceSuspended);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
