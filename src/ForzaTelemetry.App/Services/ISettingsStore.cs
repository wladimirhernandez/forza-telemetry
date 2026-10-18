namespace ForzaTelemetry.App.Services;

/// <summary>Where <see cref="AppSettings"/> live between runs. An interface so tests can use a fake or a temp file.</summary>
public interface ISettingsStore
{
    /// <summary>Reads the saved settings. Never throws: a missing, unreadable or corrupt file yields the defaults.</summary>
    AppSettings Load();

    /// <summary>Writes the settings. Never throws; returns false when the write failed (read-only disk, full drive...).</summary>
    bool TrySave(AppSettings settings);
}
