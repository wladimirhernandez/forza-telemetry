using System.Globalization;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Udp;

namespace ForzaTelemetry.App.Tests;

/// <summary>The settings model, the settings service, the units service and the language helper.</summary>
public class SettingsModelTests
{
    // ---- AppSettings --------------------------------------------------------------------------

    [Fact]
    public void Defaults_match_the_documented_first_run_values()
    {
        var d = AppSettings.Default;

        Assert.Equal(AppSettings.CurrentSchemaVersion, d.SchemaVersion);
        Assert.Equal(5300, d.UdpPort);
        Assert.Equal(UdpTelemetryListener.DefaultPort, d.UdpPort);
        Assert.Equal(SpeedUnit.Kmh, d.SpeedUnit);
        Assert.Equal(TemperatureUnit.Celsius, d.TemperatureUnit);
        Assert.Equal(AppLanguage.System, d.Language);
        Assert.False(d.StartListeningOnLaunch);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(5300, true)]
    [InlineData(65535, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(65536, false)]
    public void IsValidPort_accepts_exactly_1_to_65535(int port, bool expected) =>
        Assert.Equal(expected, AppSettings.IsValidPort(port));

    [Fact]
    public void Sanitized_resets_only_the_invalid_fields()
    {
        var dirty = new AppSettings
        {
            UdpPort = 0,
            SpeedUnit = (SpeedUnit)9,
            TemperatureUnit = TemperatureUnit.Fahrenheit,
            Language = (AppLanguage)(-3),
            StartListeningOnLaunch = true
        };

        var clean = dirty.Sanitized();

        Assert.Equal(5300, clean.UdpPort);
        Assert.Equal(SpeedUnit.Kmh, clean.SpeedUnit);
        Assert.Equal(AppLanguage.System, clean.Language);
        Assert.Equal(TemperatureUnit.Fahrenheit, clean.TemperatureUnit);
        Assert.True(clean.StartListeningOnLaunch);
    }

    // ---- SettingsService -------------------------------------------------------------------------

    private sealed class FakeStore : ISettingsStore
    {
        public List<AppSettings> Saved { get; } = [];

        public bool SaveSucceeds { get; set; } = true;

        public AppSettings Load() => AppSettings.Default;

        public bool TrySave(AppSettings settings)
        {
            Saved.Add(settings);
            return SaveSucceeds;
        }
    }

    [Fact]
    public void Update_saves_and_raises_Changed_with_the_old_and_new_value()
    {
        var store = new FakeStore();
        var service = new SettingsService(store, AppSettings.Default);
        SettingsChangedEventArgs? seen = null;
        service.Changed += (_, e) => seen = e;

        service.Update(s => s with { UdpPort = 5400 });

        Assert.Equal(5400, service.Current.UdpPort);
        Assert.Equal([service.Current], store.Saved);
        Assert.NotNull(seen);
        Assert.Equal(5300, seen.Previous.UdpPort);
        Assert.Equal(5400, seen.Current.UdpPort);
    }

    [Fact]
    public void Update_with_an_equal_value_does_nothing()
    {
        var store = new FakeStore();
        var service = new SettingsService(store, AppSettings.Default);
        var raised = 0;
        service.Changed += (_, _) => raised++;

        service.Update(s => s with { SpeedUnit = SpeedUnit.Kmh });

        Assert.Equal(0, raised);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public void Update_sanitizes_an_invalid_value()
    {
        var service = new SettingsService(new FakeStore(), AppSettings.Default with { UdpPort = 5400 });

        service.Update(s => s with { UdpPort = 70000 });

        Assert.Equal(5300, service.Current.UdpPort); // reset to the default, which differs from 5400, so it counts as a change
    }

    [Fact]
    public void A_failed_save_does_not_undo_the_change_for_this_run()
    {
        var store = new FakeStore { SaveSucceeds = false };
        var service = new SettingsService(store, AppSettings.Default);
        var raised = 0;
        service.Changed += (_, _) => raised++;

        service.Update(s => s with { SpeedUnit = SpeedUnit.Mph });

        Assert.Equal(SpeedUnit.Mph, service.Current.SpeedUnit);
        Assert.Equal(1, raised);
    }

    // ---- UnitsService ----------------------------------------------------------------------------

    [Fact]
    public void Speed_converts_km_h_to_mph()
    {
        Assert.Equal(62.1371, UnitsService.KmhToMph(100), precision: 4);
        Assert.Equal(0, UnitsService.KmhToMph(0));

        var units = new UnitsService { SpeedUnit = SpeedUnit.Mph };
        Assert.Equal("62", units.FormatSpeed(100));
        Assert.Equal("100", new UnitsService().FormatSpeed(100)); // metric stays as it is
    }

    [Fact]
    public void Temperature_converts_celsius_to_fahrenheit()
    {
        Assert.Equal(32, UnitsService.CelsiusToFahrenheit(0), precision: 6);
        Assert.Equal(212, UnitsService.CelsiusToFahrenheit(100), precision: 6);
        Assert.Equal(-40, UnitsService.CelsiusToFahrenheit(-40), precision: 6);

        var units = new UnitsService { TemperatureUnit = TemperatureUnit.Fahrenheit };
        Assert.Equal("176", units.FormatTemperature(80));
        Assert.Equal("80", new UnitsService().FormatTemperature(80));
    }

    [Fact]
    public void Labels_follow_the_chosen_units_and_announce_the_change()
    {
        var units = new UnitsService();
        var changed = new List<string?>();
        units.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.Equal(Strings.SpeedUnit, units.SpeedLabel);
        Assert.Equal(Strings.TemperatureUnit, units.TemperatureLabel);

        units.SpeedUnit = SpeedUnit.Mph;
        units.TemperatureUnit = TemperatureUnit.Fahrenheit;

        Assert.Equal("mph", units.SpeedLabel);
        Assert.Equal("°F", units.TemperatureLabel);
        Assert.Contains(nameof(UnitsService.SpeedLabel), changed);
        Assert.Contains(nameof(UnitsService.TemperatureLabel), changed);
    }

    [Fact]
    public void The_units_service_follows_the_settings()
    {
        var service = new SettingsService(new FakeStore(), AppSettings.Default);
        var units = new UnitsService(service);
        Assert.Equal(SpeedUnit.Kmh, units.SpeedUnit);

        service.Update(s => s with { SpeedUnit = SpeedUnit.Mph, TemperatureUnit = TemperatureUnit.Fahrenheit });

        Assert.Equal(SpeedUnit.Mph, units.SpeedUnit);
        Assert.Equal(TemperatureUnit.Fahrenheit, units.TemperatureUnit);
        Assert.Equal(SpeedUnit.Mph, new UnitsService(service).SpeedUnit); // and a new one starts from the stored value
    }

    // ---- Units on screen (dashboard + tires) ----------------------------------------------------------

    [Fact]
    public void The_dashboard_speed_readout_switches_units_live()
    {
        var units = new UnitsService();
        var dashboard = new DashboardViewModel(units);
        var changed = new List<string?>();
        dashboard.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        dashboard.SpeedKmh = 160;
        Assert.Equal("160", dashboard.SpeedReadout);
        Assert.Equal("km/h", dashboard.SpeedUnitLabel);

        units.SpeedUnit = SpeedUnit.Mph;

        Assert.Equal("99", dashboard.SpeedReadout);
        Assert.Equal("mph", dashboard.SpeedUnitLabel);
        Assert.Contains(nameof(DashboardViewModel.SpeedReadout), changed);
        Assert.Contains(nameof(DashboardViewModel.SpeedUnitLabel), changed);
        Assert.Equal(160f, dashboard.SpeedKmh); // the stored speed never changes, only how it is shown
    }

    [Fact]
    public void Tire_temperature_readout_switches_units_live_and_keeps_the_band()
    {
        var units = new UnitsService();
        var tire = new TireViewModel(units);
        tire.Update(wireTemperature: 212f, combinedSlip: 0.5f, suspensionTravel: 0.25f); // 100 C on the wire (F)
        var changed = new List<string?>();
        tire.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.Equal("100", tire.TemperatureReadout);
        Assert.Equal("°C", tire.TemperatureUnitLabel);

        units.TemperatureUnit = TemperatureUnit.Fahrenheit;

        Assert.Equal("212", tire.TemperatureReadout);
        Assert.Equal("°F", tire.TemperatureUnitLabel);
        Assert.Equal(TireTempBand.Warm, tire.Band);
        Assert.Contains(nameof(TireViewModel.TemperatureReadout), changed);
        Assert.Contains(nameof(TireViewModel.TemperatureUnitLabel), changed);
    }

    [Fact]
    public void A_tire_without_data_shows_a_dash_in_any_unit()
    {
        var tire = new TireViewModel(new UnitsService { TemperatureUnit = TemperatureUnit.Fahrenheit });

        Assert.Equal("--", tire.TemperatureReadout);
    }

    // ---- Language -----------------------------------------------------------------------------

    [Fact]
    public void Languages_resolve_to_their_cultures_and_System_means_leave_it_alone()
    {
        Assert.Equal("en", LanguageService.ResolveCulture(AppLanguage.English)!.Name);
        Assert.Equal("es", LanguageService.ResolveCulture(AppLanguage.Spanish)!.Name);
        Assert.Null(LanguageService.ResolveCulture(AppLanguage.System));
    }

    [Fact]
    public void Resources_resolve_in_the_forced_language()
    {
        // The same lookup the app does: a ResourceManager asked for a culture. English and Spanish differ for this key.
        Assert.Equal("Settings", Strings.ResourceManager.GetString(nameof(Strings.TabSettings), CultureInfo.GetCultureInfo("en")));
        Assert.Equal("Ajustes", Strings.ResourceManager.GetString(nameof(Strings.TabSettings), CultureInfo.GetCultureInfo("es")));
    }

    // ---- App info -----------------------------------------------------------------------------

    [Fact]
    public void AppInfo_exposes_the_name_version_and_license()
    {
        Assert.Equal(Strings.AppTitle, AppInfo.Name);
        Assert.Matches(@"^\d+\.\d+\.\d+", AppInfo.Version);
        Assert.DoesNotContain('+', AppInfo.Version);
        Assert.Equal("PolyForm Strict 1.0.0", AppInfo.LicenseName);
    }
}
