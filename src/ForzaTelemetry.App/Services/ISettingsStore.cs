namespace ForzaTelemetry.App.Services;

/// <summary>Where <see cref="AppSettings"/> live between runs. An interface so tests can use a fake or a temp file.</summary>
public interface ISettingsStore
{
    /// <summary>Reads the saved settings. Never throws: a missing, unreadable or corrupt file yields the defaults.</summary>
    AppSettings Load();

    /// <summary>
    /// True when the LAST <see cref="Load"/> fell back to the defaults because the real file could not be read (a locked or
    /// permission-denied file, a corrupt one whose backup copy failed), as opposed to a file that is simply not there. Writing the
    /// defaults over such a file would destroy the user's real choices, so the settings service stops writing for the session.
    /// Stores that cannot tell keep the default: false.
    /// </summary>
    bool LoadFailedToRead => false;

    /// <summary>Writes the settings. Never throws; returns false when the write failed (read-only disk, full drive...).</summary>
    bool TrySave(AppSettings settings);
}
