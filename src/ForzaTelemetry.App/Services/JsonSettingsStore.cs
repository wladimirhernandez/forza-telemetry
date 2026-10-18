using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForzaTelemetry.App.Services;

/// <summary>
/// Stores <see cref="AppSettings"/> as a JSON file, by default <c>%APPDATA%\ForzaTelemetry\settings.json</c>.
/// </summary>
/// <remarks>
/// <b>Why %APPDATA% (Roaming).</b> Settings are a small per-user preference file, which is what the roaming profile is for;
/// the logs go to LocalAppData because they are machine-specific and can grow. Neither location needs admin rights, unlike
/// the install folder under Program Files.
/// <para>
/// <b>Why the write goes through a temp file.</b> Overwriting <c>settings.json</c> in place and being interrupted (crash,
/// power loss) leaves a half-written file, which then reads as corrupt and silently resets the user's settings. Writing the
/// full text to <c>settings.json.tmp</c> first and then swapping it in (<see cref="File.Replace(string, string, string?)"/>,
/// an atomic operation on NTFS) means the real file is always either the old complete version or the new complete one.
/// </para>
/// </remarks>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly ILogger _logger;

    /// <param name="path">Full path of the settings file.</param>
    /// <param name="logger">Receives load/save outcomes; optional.</param>
    public JsonSettingsStore(string path, ILogger? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary><c>%APPDATA%\ForzaTelemetry\settings.json</c>.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ForzaTelemetry", "settings.json");

    public AppSettings Load()
    {
        string json;
        try
        {
            if (!File.Exists(_path))
            {
                AppLog.SettingsMissing(_logger, _path);
                return AppSettings.Default;
            }

            json = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.SettingsUnreadable(_logger, ex, _path);
            return AppSettings.Default;
        }

        try
        {
            // Field-by-field read (see AppSettingsReader): a bad value costs that value only. What still throws here is a
            // file that is not JSON or not an object, which is genuinely corrupt.
            var result = AppSettingsReader.Read(json);

            if (result.FileSchemaVersion > AppSettings.CurrentSchemaVersion)
            {
                // Written by a NEWER version of the app. Known fields still apply; unknown ones are kept in
                // ExtensionData so saving this file does not erase them for that newer version.
                AppLog.SettingsFromNewerVersion(_logger, result.FileSchemaVersion, AppSettings.CurrentSchemaVersion);
            }

            foreach (var field in result.RejectedFields)
            {
                AppLog.SettingsFieldRejected(_logger, field, _path);
            }

            // (Place for future migrations: if result.FileSchemaVersion < CurrentSchemaVersion, upgrade the values here.)
            var settings = result.Settings.Sanitized();
            AppLog.SettingsLoaded(_logger, _path);
            return settings;
        }
        catch (JsonException ex)
        {
            AppLog.SettingsCorrupt(_logger, ex, _path);
            KeepCorruptCopy();
            return AppSettings.Default;
        }
    }

    public bool TrySave(AppSettings settings)
    {
        var temp = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // Always this build's layout version, whatever the value in hand says (it may have come from a newer file).
            var toWrite = settings with { SchemaVersion = AppSettings.CurrentSchemaVersion };
            File.WriteAllText(temp, Serialize(toWrite));

            if (File.Exists(_path))
            {
                File.Replace(temp, _path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, _path);
            }

            AppLog.SettingsSaved(_logger, _path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.SettingsSaveFailed(_logger, ex, _path);
            TryDelete(temp);
            return false;
        }
    }

    /// <summary>
    /// The known settings through the source-generated serializer, plus every property this build did not recognize when it
    /// read the file (<see cref="AppSettings.ExtensionData"/>), copied through untouched. A known name always wins over an
    /// unknown one of the same name.
    /// </summary>
    private static string Serialize(AppSettings settings)
    {
        var node = JsonSerializer.SerializeToNode(settings, SettingsJsonContext.Default.AppSettings)!.AsObject();

        if (settings.ExtensionData is { } extra)
        {
            foreach (var (name, value) in extra)
            {
                node.TryAdd(name, JsonNode.Parse(value.GetRawText()));
            }
        }

        return node.ToJsonString(SettingsJsonContext.Default.Options);
    }

    /// <summary>
    /// Before the defaults overwrite a corrupt file on the next save, keep it next to the original so the user (or a bug
    /// report) can still see what was in it. Best effort: failing to keep the copy must not stop the app from starting.
    /// </summary>
    private void KeepCorruptCopy()
    {
        try
        {
            File.Copy(_path, _path + ".corrupt", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more to do: the warning above already recorded the corruption.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .tmp is harmless: the next save overwrites it.
        }
    }
}
