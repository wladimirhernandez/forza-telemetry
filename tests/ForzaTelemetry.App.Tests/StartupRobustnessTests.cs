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
}
