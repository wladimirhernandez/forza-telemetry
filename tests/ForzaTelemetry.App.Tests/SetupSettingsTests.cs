using System.IO;
using ForzaTelemetry.App.Services;

namespace ForzaTelemetry.App.Tests;

/// <summary>The persisted "setup completed" flag of the connection guide.</summary>
public sealed class SetupSettingsTests : IDisposable
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
    public void A_fresh_install_has_not_completed_setup() => Assert.False(AppSettings.Default.HasCompletedSetup);

    [Fact]
    public void The_flag_round_trips_through_the_file()
    {
        var store = new JsonSettingsStore(SettingsPath);

        Assert.True(store.TrySave(AppSettings.Default with { HasCompletedSetup = true }));

        Assert.True(new JsonSettingsStore(SettingsPath).Load().HasCompletedSetup);
        Assert.Contains("\"hasCompletedSetup\": true", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void A_file_from_before_the_flag_existed_means_setup_not_completed_and_keeps_the_other_choices()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{ "schemaVersion": 1, "udpPort": 5400, "speedUnit": "mph" }""");

        var loaded = new JsonSettingsStore(SettingsPath).Load();

        Assert.False(loaded.HasCompletedSetup);
        Assert.Equal(5400, loaded.UdpPort);
        Assert.Equal(SpeedUnit.Mph, loaded.SpeedUnit);
    }

    [Fact]
    public void A_flag_of_the_wrong_type_is_ignored_without_losing_the_rest()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{ "udpPort": 5400, "hasCompletedSetup": "yes" }""");

        var loaded = new JsonSettingsStore(SettingsPath).Load();

        Assert.False(loaded.HasCompletedSetup);
        Assert.Equal(5400, loaded.UdpPort);
    }
}
