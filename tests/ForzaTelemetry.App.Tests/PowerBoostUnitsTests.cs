using System.Globalization;
using System.IO;
using ForzaTelemetry.App.Controls;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using ForzaTelemetry.Core.Insights;

namespace ForzaTelemetry.App.Tests;

/// <summary>The power (kW / hp) and boost (psi / bar) unit preferences: conversion, text, screens, settings file.</summary>
public sealed class PowerBoostUnitsTests : IDisposable
{
    private sealed class MemoryStore : ISettingsStore
    {
        public List<AppSettings> Saved { get; } = [];

        public AppSettings Load() => AppSettings.Default;

        public bool TrySave(AppSettings settings)
        {
            Saved.Add(settings);
            return true;
        }
    }

    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ForzaTelemetryTests", Guid.NewGuid().ToString("N"));

    public PowerBoostUnitsTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static (UnitsService Units, SettingsService Settings, MemoryStore Store) Rig(AppSettings? initial = null)
    {
        var store = new MemoryStore();
        var settings = new SettingsService(store, initial ?? AppSettings.Default);
        return (new UnitsService(settings), settings, store);
    }

    // ---- Conversions -----------------------------------------------------------------------------

    [Fact]
    public void The_defaults_are_kilowatts_and_psi()
    {
        Assert.Equal(PowerUnit.Kw, AppSettings.Default.PowerUnit);
        Assert.Equal(BoostUnit.Psi, AppSettings.Default.BoostUnit);

        var units = new UnitsService();
        Assert.Equal("kW", units.PowerLabel);
        Assert.Equal("hp", units.AltPowerLabel);
        Assert.Equal("psi", units.BoostLabel);
    }

    [Fact]
    public void One_bar_is_14_5037738_psi_and_one_kilowatt_is_1_341022_horsepower()
    {
        Assert.Equal(14.5037738, UnitsService.PsiPerBar);
        Assert.Equal(1.0, UnitsService.PsiToBar(14.5037738), precision: 12);
        Assert.Equal(1.0, UnitsService.PsiToBar(UnitsService.PsiPerBar), precision: 12);
        Assert.Equal(0, UnitsService.PsiToBar(0));
        Assert.Equal(1.341022, UnitsService.KilowattsToHorsepower(1), precision: 5);
        Assert.Equal(134.1022, UnitsService.KilowattsToHorsepower(100), precision: 3);
        Assert.Equal(0, UnitsService.KilowattsToHorsepower(0));
    }

    [Fact]
    public void Power_and_boost_convert_to_the_chosen_unit_and_the_other_power_unit()
    {
        var (units, settings, _) = Rig();

        Assert.Equal(200, units.ConvertPower(200));
        Assert.Equal(268.2044, units.ConvertAltPower(200), precision: 3);
        Assert.Equal(14.5037738, units.ConvertBoost(14.5037738), precision: 9);

        settings.Update(s => s with { PowerUnit = PowerUnit.Hp, BoostUnit = BoostUnit.Bar });

        Assert.Equal(268.2044, units.ConvertPower(200), precision: 3);
        Assert.Equal(200, units.ConvertAltPower(200));
        Assert.Equal(1.0, units.ConvertBoost(14.5037738), precision: 9);
        Assert.Equal("hp", units.PowerLabel);
        Assert.Equal("kW", units.AltPowerLabel);
        Assert.Equal("bar", units.BoostLabel);
    }

    [Theory]
    [InlineData(PowerUnit.Kw, 200.4, "200", "269")]
    [InlineData(PowerUnit.Hp, 200.4, "269", "200")]
    [InlineData(PowerUnit.Kw, 0, "0", "0")]
    public void Power_text_has_no_decimals_in_either_unit(PowerUnit unit, double kilowatts, string main, string alt)
    {
        var (units, _, _) = Rig(new AppSettings { PowerUnit = unit });

        Assert.Equal(main, units.FormatPower(kilowatts));
        Assert.Equal(alt, units.FormatAltPower(kilowatts));
    }

    [Theory]
    [InlineData(BoostUnit.Psi, 12.54, "12.5")]
    [InlineData(BoostUnit.Psi, 0, "0.0")]
    [InlineData(BoostUnit.Bar, 14.5037738, "1.00")]
    [InlineData(BoostUnit.Bar, 21.756, "1.50")]
    [InlineData(BoostUnit.Bar, 0, "0.00")]
    public void Boost_text_has_one_decimal_in_psi_and_two_in_bar(BoostUnit unit, double psi, string expected)
    {
        var (units, _, _) = Rig(new AppSettings { BoostUnit = unit });

        Assert.Equal(expected, units.FormatBoost(psi));
    }

    [Fact]
    public void The_text_follows_the_culture_decimal_separator()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es");
        var (units, _, _) = Rig(new AppSettings { BoostUnit = BoostUnit.Bar });

        Assert.Equal("1,00", units.FormatBoost(14.5037738));
    }

    [Fact]
    public void The_affects_filters_tell_the_families_apart()
    {
        Assert.True(UnitsService.AffectsPower(nameof(UnitsService.PowerUnit)));
        Assert.True(UnitsService.AffectsPower(null));
        Assert.False(UnitsService.AffectsPower(nameof(UnitsService.BoostUnit)));
        Assert.True(UnitsService.AffectsBoost(nameof(UnitsService.BoostUnit)));
        Assert.True(UnitsService.AffectsBoost(""));
        Assert.False(UnitsService.AffectsBoost(nameof(UnitsService.PowerUnit)));
        // Only the primary property: the derived labels notify too, and matching them would re-raise the readouts again for one change.
        Assert.False(UnitsService.AffectsPower(nameof(UnitsService.PowerLabel)));
        Assert.False(UnitsService.AffectsPower(nameof(UnitsService.AltPowerLabel)));
        Assert.False(UnitsService.AffectsBoost(nameof(UnitsService.BoostLabel)));
        Assert.False(UnitsService.AffectsSpeed(nameof(UnitsService.PowerUnit)));
    }

    // ---- Dashboard engine card ---------------------------------------------------------------------

    private static TelemetryPacket DrivingPacket() =>
        TestPackets.Sample() with { Power = 200_000f, Boost = 14.5037738f };

    [Fact]
    public void The_dashboard_shows_power_and_boost_in_the_chosen_units_and_follows_a_change_live()
    {
        var (units, settings, _) = Rig();
        var dashboard = new DashboardViewModel(units);
        dashboard.Apply(DrivingPacket());
        var changed = new List<string?>();
        dashboard.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal("200", dashboard.PowerReadout);
        Assert.Equal("kW", dashboard.PowerUnitLabel);
        Assert.Equal("268", dashboard.AltPowerReadout);
        Assert.Equal("hp", dashboard.AltPowerUnitLabel);
        Assert.Equal("14.5", dashboard.BoostReadout);
        Assert.Equal("psi", dashboard.BoostUnitLabel);

        settings.Update(s => s with { PowerUnit = PowerUnit.Hp, BoostUnit = BoostUnit.Bar });

        Assert.Equal("268", dashboard.PowerReadout);
        Assert.Equal("hp", dashboard.PowerUnitLabel);
        Assert.Equal("200", dashboard.AltPowerReadout);
        Assert.Equal("kW", dashboard.AltPowerUnitLabel);
        Assert.Equal("1.00", dashboard.BoostReadout);
        Assert.Equal("bar", dashboard.BoostUnitLabel);
        Assert.Contains(nameof(DashboardViewModel.PowerReadout), changed);
        Assert.Contains(nameof(DashboardViewModel.BoostUnitLabel), changed);
        Assert.DoesNotContain(nameof(DashboardViewModel.SpeedReadout), changed); // a power change does not re-render the speed
    }

    [Fact]
    public void One_unit_change_raises_each_dependent_readout_exactly_once()
    {
        var (units, settings, _) = Rig();
        var dashboard = new DashboardViewModel(units);
        var tuning = new TuningViewModel(new DynoCapture(), new TelemetryHistory(), new SuspensionBottomingCounter(), units);
        var dashboardChanges = new List<string?>();
        var tuningChanges = new List<string?>();
        dashboard.PropertyChanged += (_, e) => dashboardChanges.Add(e.PropertyName);
        tuning.PropertyChanged += (_, e) => tuningChanges.Add(e.PropertyName);

        settings.Update(s => s with { PowerUnit = PowerUnit.Hp });

        Assert.Equal(1, dashboardChanges.Count(n => n == nameof(DashboardViewModel.PowerReadout)));
        Assert.Equal(1, dashboardChanges.Count(n => n == nameof(DashboardViewModel.PowerUnitLabel)));
        Assert.Equal(1, dashboardChanges.Count(n => n == nameof(DashboardViewModel.AltPowerReadout)));
        Assert.Equal(1, dashboardChanges.Count(n => n == nameof(DashboardViewModel.AltPowerUnitLabel)));
        Assert.Equal(1, tuningChanges.Count(n => n == nameof(TuningViewModel.PowerUnit)));

        dashboardChanges.Clear();
        settings.Update(s => s with { BoostUnit = BoostUnit.Bar });

        Assert.Equal(1, dashboardChanges.Count(n => n == nameof(DashboardViewModel.BoostReadout)));
        Assert.Equal(1, dashboardChanges.Count(n => n == nameof(DashboardViewModel.BoostUnitLabel)));
    }

    [Fact]
    public void There_is_one_horsepower_factor_for_the_dashboard_the_setting_and_the_dyno()
    {
        const double kilowatts = 287.3;

        Assert.Equal(DashboardMath.ToHorsepower(kilowatts), UnitsService.KilowattsToHorsepower(kilowatts));
        Assert.Equal(DashboardMath.ToHorsepower(kilowatts), DynoMath.ToHorsepower(kilowatts * 1000), precision: 9);
    }

    [Fact]
    public void A_new_packet_updates_the_readouts_and_the_raw_values_stay_in_kw_and_psi()
    {
        var (units, _, _) = Rig(new AppSettings { PowerUnit = PowerUnit.Hp, BoostUnit = BoostUnit.Bar });
        var dashboard = new DashboardViewModel(units);
        var changed = new List<string?>();
        dashboard.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        dashboard.Apply(DrivingPacket());

        Assert.Equal(200.0, dashboard.PowerKw, precision: 3); // gauges and tests keep the base units
        Assert.Equal(14.5037738, dashboard.BoostPsi, precision: 5);
        Assert.Equal("268", dashboard.PowerReadout);
        Assert.Contains(nameof(DashboardViewModel.PowerReadout), changed);
        Assert.Contains(nameof(DashboardViewModel.BoostReadout), changed);
    }

    [Fact]
    public void The_idle_dashboard_reads_zero_in_whatever_unit_is_chosen()
    {
        var (units, _, _) = Rig(new AppSettings { PowerUnit = PowerUnit.Hp, BoostUnit = BoostUnit.Bar });
        var dashboard = new DashboardViewModel(units);
        dashboard.Apply(DrivingPacket());

        dashboard.ResetLive();

        Assert.Equal("0", dashboard.PowerReadout);
        Assert.Equal("0", dashboard.AltPowerReadout);
        Assert.Equal("0.00", dashboard.BoostReadout);
    }

    // ---- Tuning dyno ------------------------------------------------------------------------------

    [Fact]
    public void The_tuning_page_exposes_the_power_unit_and_announces_a_change()
    {
        var (units, settings, _) = Rig();
        var tuning = new TuningViewModel(new DynoCapture(), new TelemetryHistory(), new SuspensionBottomingCounter(), units);
        var changed = new List<string?>();
        tuning.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal(PowerUnit.Kw, tuning.PowerUnit);

        settings.Update(s => s with { PowerUnit = PowerUnit.Hp });

        Assert.Equal(PowerUnit.Hp, tuning.PowerUnit);
        Assert.Contains(nameof(TuningViewModel.PowerUnit), changed);

        changed.Clear();
        settings.Update(s => s with { BoostUnit = BoostUnit.Bar }); // not a dyno concern
        Assert.DoesNotContain(nameof(TuningViewModel.PowerUnit), changed);
    }

    [Fact]
    public void The_dyno_text_has_both_unit_variants_in_both_languages()
    {
        Assert.Equal("kW", InsightsStrings.DynoPowerAxis);
        Assert.Equal("hp", InsightsStrings.DynoPowerAxisHp);
        Assert.Equal("{0} kW ({1} hp)", InsightsStrings.DynoPeakPowerFormat);
        Assert.Equal("{0} hp ({1} kW)", InsightsStrings.DynoPeakPowerHpFormat);
        Assert.Equal("bar", Strings.BoostUnitBar);
    }

    // ---- Settings screen ---------------------------------------------------------------------------

    [Fact]
    public void The_settings_screen_edits_both_units_and_saves_each_change()
    {
        var store = new MemoryStore();
        var settings = new SettingsService(store, AppSettings.Default);
        var vm = new SettingsViewModel(settings, new DataOutViewModel(AppSettings.Default.UdpPort), @"C:\logs");

        Assert.Equal(PowerUnit.Kw, vm.PowerUnit);
        Assert.Equal(BoostUnit.Psi, vm.BoostUnit);
        Assert.Equal(["kW", "hp"], vm.PowerUnitOptions.Select(o => o.Label));
        Assert.Equal(["psi", "bar"], vm.BoostUnitOptions.Select(o => o.Label));

        vm.PowerUnit = PowerUnit.Hp;
        vm.BoostUnit = BoostUnit.Bar;

        Assert.Equal(PowerUnit.Hp, settings.Current.PowerUnit);
        Assert.Equal(BoostUnit.Bar, settings.Current.BoostUnit);
        Assert.Equal(2, store.Saved.Count);
    }

    [Fact]
    public void The_settings_screen_starts_from_the_saved_units()
    {
        var settings = new SettingsService(new MemoryStore(), new AppSettings { PowerUnit = PowerUnit.Hp, BoostUnit = BoostUnit.Bar });
        var vm = new SettingsViewModel(settings, new DataOutViewModel(AppSettings.Default.UdpPort), @"C:\logs");

        Assert.Equal(PowerUnit.Hp, vm.PowerUnit);
        Assert.Equal(BoostUnit.Bar, vm.BoostUnit);
    }

    // ---- Settings file -----------------------------------------------------------------------------

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public void The_units_survive_a_save_and_load_and_are_written_by_camel_case_name()
    {
        var store = new JsonSettingsStore(SettingsPath);

        Assert.True(store.TrySave(new AppSettings { PowerUnit = PowerUnit.Hp, BoostUnit = BoostUnit.Bar }));
        var loaded = new JsonSettingsStore(SettingsPath).Load();

        Assert.Equal(PowerUnit.Hp, loaded.PowerUnit);
        Assert.Equal(BoostUnit.Bar, loaded.BoostUnit);
        var text = File.ReadAllText(SettingsPath);
        Assert.Contains("\"powerUnit\": \"hp\"", text);
        Assert.Contains("\"boostUnit\": \"bar\"", text);
    }

    [Fact]
    public void A_file_written_before_these_units_existed_gets_kw_and_psi_and_keeps_the_rest()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{ "schemaVersion": 1, "udpPort": 5400, "speedUnit": "mph", "temperatureUnit": "fahrenheit" }""");

        var loaded = new JsonSettingsStore(SettingsPath).Load();

        Assert.Equal(PowerUnit.Kw, loaded.PowerUnit);
        Assert.Equal(BoostUnit.Psi, loaded.BoostUnit);
        Assert.Equal(5400, loaded.UdpPort);
        Assert.Equal(SpeedUnit.Mph, loaded.SpeedUnit);
        Assert.Equal(TemperatureUnit.Fahrenheit, loaded.TemperatureUnit);
    }

    [Theory]
    [InlineData("\"horsepower\"")]
    [InlineData("\"42\"")]
    [InlineData("7")]
    [InlineData("true")]
    [InlineData("null")]
    public void A_bad_unit_costs_only_that_unit(string badValue)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, $$"""{ "powerUnit": {{badValue}}, "boostUnit": "BAR", "speedUnit": "mph" }""");

        var loaded = new JsonSettingsStore(SettingsPath).Load();

        Assert.Equal(PowerUnit.Kw, loaded.PowerUnit); // back to the default
        Assert.Equal(BoostUnit.Bar, loaded.BoostUnit); // any casing of a defined name
        Assert.Equal(SpeedUnit.Mph, loaded.SpeedUnit);
    }

    [Fact]
    public void Sanitizing_puts_an_undefined_unit_back_to_its_default()
    {
        var clean = (AppSettings.Default with { PowerUnit = (PowerUnit)9, BoostUnit = (BoostUnit)(-1) }).Sanitized();

        Assert.Equal(PowerUnit.Kw, clean.PowerUnit);
        Assert.Equal(BoostUnit.Psi, clean.BoostUnit);
    }
}
