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
    [InlineData("")]
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

    [Theory]
    [InlineData("{ \"udpPort\": \"five thousand\", \"speedUnit\": \"mph\", \"startListeningOnLaunch\": true }")] // string port
    [InlineData("{ \"udpPort\": 5300.5, \"speedUnit\": \"mph\", \"startListeningOnLaunch\": true }")] // not a whole number
    [InlineData("{ \"udpPort\": null, \"speedUnit\": \"mph\", \"startListeningOnLaunch\": true }")]
    [InlineData("{ \"udpPort\": 70000, \"speedUnit\": \"mph\", \"startListeningOnLaunch\": true }")] // out of range
    public void A_bad_port_costs_only_the_port(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, content);
        var logger = new ListLogger<JsonSettingsStore>();

        var loaded = new JsonSettingsStore(SettingsPath, logger).Load();

        Assert.Equal(AppSettings.Default.UdpPort, loaded.UdpPort);
        Assert.Equal(SpeedUnit.Mph, loaded.SpeedUnit); // the good values survive
        Assert.True(loaded.StartListeningOnLaunch);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("udpPort"));
        Assert.False(File.Exists(SettingsPath + ".corrupt")); // not corrupt: nothing to keep a copy of
    }

    [Theory]
    [InlineData("furlongs")]
    [InlineData("42")] // a numeric STRING is not an enum name
    [InlineData("")]
    public void An_unknown_enum_name_costs_only_that_setting(string badName)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            SettingsPath,
            $$"""{ "speedUnit": "{{badName}}", "temperatureUnit": "fahrenheit", "language": "spanish", "udpPort": 5400 }""");

        var loaded = new JsonSettingsStore(SettingsPath).Load();

        Assert.Equal(SpeedUnit.Kmh, loaded.SpeedUnit);
        Assert.Equal(TemperatureUnit.Fahrenheit, loaded.TemperatureUnit);
        Assert.Equal(AppLanguage.Spanish, loaded.Language);
        Assert.Equal(5400, loaded.UdpPort);
    }

    [Fact]
    public void A_wrong_type_for_a_boolean_or_an_enum_is_rejected_per_field()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            SettingsPath, """{ "startListeningOnLaunch": "yes", "language": [1], "temperatureUnit": true, "udpPort": 6000 }""");

        var loaded = new JsonSettingsStore(SettingsPath).Load();

        Assert.False(loaded.StartListeningOnLaunch);
        Assert.Equal(AppLanguage.System, loaded.Language);
        Assert.Equal(TemperatureUnit.Celsius, loaded.TemperatureUnit);
        Assert.Equal(6000, loaded.UdpPort);
    }

    [Fact]
    public void Property_names_and_enum_values_are_read_case_insensitively_including_legacy_PascalCase()
    {
        Directory.CreateDirectory(_directory);
        // What the previous build wrote (PascalCase enum values) plus a hand-edited casing of the keys.
        File.WriteAllText(
            SettingsPath,
            """{ "SchemaVersion": 1, "UDPPORT": 5401, "speedUnit": "Mph", "temperatureUnit": "FAHRENHEIT", "language": "Spanish", "startListeningOnLaunch": true }""");

        var loaded = new JsonSettingsStore(SettingsPath).Load();

        Assert.Equal(
            new AppSettings
            {
                UdpPort = 5401,
                SpeedUnit = SpeedUnit.Mph,
                TemperatureUnit = TemperatureUnit.Fahrenheit,
                Language = AppLanguage.Spanish,
                StartListeningOnLaunch = true
            },
            loaded with { ExtensionData = null });
    }

    [Fact]
    public void Enums_are_written_in_camelCase()
    {
        new JsonSettingsStore(SettingsPath).TrySave(
            AppSettings.Default with { TemperatureUnit = TemperatureUnit.Fahrenheit, Language = AppLanguage.Spanish });

        var text = File.ReadAllText(SettingsPath);

        Assert.Contains("\"temperatureUnit\": \"fahrenheit\"", text);
        Assert.Contains("\"language\": \"spanish\"", text);
        Assert.Contains("\"speedUnit\": \"kmh\"", text);
    }

    [Fact]
    public void Saving_always_writes_the_current_schema_version_even_for_a_value_that_carries_another()
    {
        var store = new JsonSettingsStore(SettingsPath);

        store.TrySave(AppSettings.Default with { SchemaVersion = 99 });

        Assert.Contains($"\"schemaVersion\": {AppSettings.CurrentSchemaVersion}", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Loading_a_newer_file_and_saving_it_back_keeps_the_fields_this_build_does_not_know()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            SettingsPath,
            """{ "schemaVersion": 99, "udpPort": 5555, "theme": "midnight", "overlay": { "x": 10, "enabled": true }, "tags": [1, 2] }""");
        var store = new JsonSettingsStore(SettingsPath);

        var loaded = store.Load();
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion); // this build's layout, not the file's claim
        Assert.True(store.TrySave(loaded with { SpeedUnit = SpeedUnit.Mph }));

        using var written = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SettingsPath));
        var root = written.RootElement;
        Assert.Equal(AppSettings.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(5555, root.GetProperty("udpPort").GetInt32());
        Assert.Equal("mph", root.GetProperty("speedUnit").GetString());
        Assert.Equal("midnight", root.GetProperty("theme").GetString()); // the newer version's data is still there
        Assert.True(root.GetProperty("overlay").GetProperty("enabled").GetBoolean());
        Assert.Equal(2, root.GetProperty("tags").GetArrayLength());
    }

    [Fact]
    public void Unknown_fields_survive_a_change_made_through_the_settings_service()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{ "udpPort": 5300, "futureThing": 7 }""");
        var store = new JsonSettingsStore(SettingsPath);
        var service = new SettingsService(store, store.Load());

        service.Update(s => s with { UdpPort = 5400 }); // Sanitized() copies keep ExtensionData

        using var written = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.Equal(5400, written.RootElement.GetProperty("udpPort").GetInt32());
        Assert.Equal(7, written.RootElement.GetProperty("futureThing").GetInt32());
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
        Assert.Contains("\"speedUnit\": \"mph\"", text); // camelCase, like the keys
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
