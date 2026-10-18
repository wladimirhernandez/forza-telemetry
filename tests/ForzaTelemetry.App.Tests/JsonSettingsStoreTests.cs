using System.IO;
using ForzaTelemetry.App.Services;
using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.App.Tests;

/// <summary>Round trips, defaults and failure handling of the JSON settings file. Every test uses its own temp folder, never the real %APPDATA%.</summary>
public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ForzaTelemetryTests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Save_then_Load_round_trips_every_field()
    {
        var store = new JsonSettingsStore(SettingsPath);
        var settings = new AppSettings
        {
            UdpPort = 5400,
            SpeedUnit = SpeedUnit.Mph,
            TemperatureUnit = TemperatureUnit.Fahrenheit,
            Language = AppLanguage.Spanish,
            StartListeningOnLaunch = true
        };

        Assert.True(store.TrySave(settings));

        Assert.Equal(settings, new JsonSettingsStore(SettingsPath).Load());
    }

    [Fact]
    public void A_missing_file_gives_the_defaults_and_is_logged()
    {
        var logger = new ListLogger<JsonSettingsStore>();

        var loaded = new JsonSettingsStore(SettingsPath, logger).Load();

        Assert.Equal(AppSettings.Default, loaded);
        Assert.False(File.Exists(SettingsPath)); // loading never creates the file
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("defaults"));
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("{ \"udpPort\": ")]
    [InlineData("null")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"udpPort\": \"five thousand\" }")]
    [InlineData("{ \"speedUnit\": \"furlongs\" }")]
    public void A_corrupt_file_gives_the_defaults_logs_a_warning_and_keeps_a_copy(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, content);
        var logger = new ListLogger<JsonSettingsStore>();

        var loaded = new JsonSettingsStore(SettingsPath, logger).Load();

        Assert.Equal(AppSettings.Default, loaded);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(content, File.ReadAllText(SettingsPath + ".corrupt")); // the user's data is not silently destroyed
    }

    [Fact]
    public void Out_of_range_values_fall_back_one_field_at_a_time()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{ "udpPort": 70000, "speedUnit": "Mph", "language": 42, "startListeningOnLaunch": true }""");

        var loaded = new JsonSettingsStore(SettingsPath).Load();

        Assert.Equal(AppSettings.Default.UdpPort, loaded.UdpPort); // bad port reset...
        Assert.Equal(AppLanguage.System, loaded.Language); // ...and a number that is no language...
        Assert.Equal(SpeedUnit.Mph, loaded.SpeedUnit); // ...without discarding the good values
        Assert.True(loaded.StartListeningOnLaunch);
    }

    [Fact]
    public void Missing_fields_take_their_defaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{ "speedUnit": "Mph" }""");

        var loaded = new JsonSettingsStore(SettingsPath).Load();

        Assert.Equal(AppSettings.Default with { SpeedUnit = SpeedUnit.Mph }, loaded);
    }

    [Fact]
    public void A_file_from_a_newer_version_still_loads_and_warns()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{ "schemaVersion": 99, "udpPort": 5555, "somethingNew": true }""");
        var logger = new ListLogger<JsonSettingsStore>();

        var loaded = new JsonSettingsStore(SettingsPath, logger).Load();

        Assert.Equal(5555, loaded.UdpPort);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("newer"));
    }

    [Fact]
    public void The_file_is_readable_json_with_named_enums_and_a_schema_version()
    {
        new JsonSettingsStore(SettingsPath).TrySave(AppSettings.Default with { SpeedUnit = SpeedUnit.Mph });

        var text = File.ReadAllText(SettingsPath);

        Assert.Contains("\"schemaVersion\": 1", text);
        Assert.Contains("\"speedUnit\": \"Mph\"", text);
        Assert.Contains("\"udpPort\": 5300", text);
    }

    [Fact]
    public void Saving_over_an_existing_file_replaces_it_and_leaves_no_temp_file()
    {
        var store = new JsonSettingsStore(SettingsPath);
        store.TrySave(AppSettings.Default);

        Assert.True(store.TrySave(AppSettings.Default with { UdpPort = 6000 }));

        Assert.Equal(6000, store.Load().UdpPort);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void Save_creates_the_missing_folder()
    {
        Assert.False(Directory.Exists(_directory));

        Assert.True(new JsonSettingsStore(SettingsPath).TrySave(AppSettings.Default));

        Assert.True(File.Exists(SettingsPath));
    }

    [Fact]
    public void A_failed_save_returns_false_and_logs_instead_of_throwing()
    {
        // The settings "file" is an existing DIRECTORY, so the swap-in step cannot succeed.
        Directory.CreateDirectory(SettingsPath);
        var logger = new ListLogger<JsonSettingsStore>();

        var saved = new JsonSettingsStore(SettingsPath, logger).TrySave(AppSettings.Default);

        Assert.False(saved);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void The_default_path_is_settings_json_under_ForzaTelemetry_in_AppData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ForzaTelemetry", "settings.json");

        Assert.Equal(expected, JsonSettingsStore.DefaultPath);
    }
}
