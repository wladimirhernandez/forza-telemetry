using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForzaTelemetry.App.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForzaTelemetry.App.Services;

/// <summary>Why a car name was refused (see <see cref="CarNameStore.Validate"/>).</summary>
public enum CarNameProblem
{
    /// <summary>The name is acceptable.</summary>
    None = 0,

    /// <summary>Nothing left after trimming.</summary>
    Empty,

    /// <summary>Longer than <see cref="CarNameStore.MaxLength"/> characters.</summary>
    TooLong
}

/// <summary>
/// The nicknames players give their cars, kept in memory and in <c>%APPDATA%\ForzaTelemetry\cars.json</c>.
/// </summary>
/// <remarks>
/// <b>Why nicknames on top of the catalog.</b> The game identifies a car by a number (<c>CarOrdinal</c>) and the telemetry spec does not
/// give the names. The app ships a community-maintained name list (<see cref="CarCatalog"/>), but it can lag behind new cars and players
/// often want their own names; a nickname always wins over the catalog name (see <see cref="CarNames"/>).
/// <para>
/// <b>The file.</b> <c>{ "schemaVersion": 1, "cars": { "2211": "Daily GT" } }</c>: ordinal to name, one entry per car MODEL (an upgraded
/// version of the same car keeps its name). Same care as the other JSON files (see <see cref="AtomicFile"/>): writes go through a temp file,
/// so an interruption cannot leave half a file; reads are tolerant, so one bad entry costs only that entry, and a file that is not JSON at
/// all is kept next to the original as <c>.corrupt</c> and replaced by an empty list on the next save (when even that copy cannot be made, nothing is written
/// for the session, like a file that could not be read).
/// </para>
/// <para>
/// <b>Newer files.</b> Same rule as the settings file: a top-level field this build does not know (written by a newer version) is kept
/// exactly as read and written back next to <c>cars</c>, and the version written is always this build's own, so a downgrade does not
/// erase what the newer one added. A file that could not even be READ (permissions, a sharing violation) is never written over: the names
/// still work in memory for the session, but the file stays as it was.
/// </para>
/// <para>
/// Not thread-safe: like the view models, it is used from the UI thread only. A failed save is logged and does not undo the change: the name
/// still applies until the app closes, exactly like a setting that could not be written.
/// </para>
/// </remarks>
public sealed class CarNameStore
{
    /// <summary>The layout version of the file this build reads and writes.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The longest name accepted, in characters. Long enough for "Mustang Mach 1 (drift tune)", short enough for a table cell.</summary>
    public const int MaxLength = 40;

    // Indented because the file is meant to be readable and hand-editable. The relaxed encoder keeps accents as typed.
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _path;
    private readonly ILogger _logger;
    private Dictionary<int, string> _names = [];

    // Top-level properties of the file this build does not know, kept as read so a save writes them back.
    private Dictionary<string, JsonElement>? _unknownFields;

    // True when the file exists but could not be read: writing then would replace data this build never saw.
    private bool _writeSuspended;

    /// <param name="path">Full path of the names file.</param>
    /// <param name="logger">Receives load/save outcomes; optional.</param>
    public CarNameStore(string path, ILogger? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary><c>%APPDATA%\ForzaTelemetry\cars.json</c>.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ForzaTelemetry", "cars.json");

    /// <summary>Raised after a name was set, changed or removed (not on <see cref="Load"/>).</summary>
    public event EventHandler? Changed;

    /// <summary>The nickname of a car model, or null when the player never named it.</summary>
    public string? Find(int carOrdinal) => _names.GetValueOrDefault(carOrdinal);

    /// <summary>How many cars have a nickname.</summary>
    public int Count => _names.Count;

    /// <summary>
    /// Checks a name as typed. <paramref name="name"/> is the cleaned-up version to store: control characters (a pasted line break) become
    /// spaces and the ends are trimmed. Internal runs of spaces are kept as typed.
    /// </summary>
    public static CarNameProblem Validate(string? raw, out string name)
    {
        var builder = new StringBuilder(raw?.Length ?? 0);
        foreach (var c in raw ?? "")
        {
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        name = builder.ToString().Trim();
        return name.Length == 0 ? CarNameProblem.Empty : name.Length > MaxLength ? CarNameProblem.TooLong : CarNameProblem.None;
    }

    /// <summary>The localized sentence for a problem, for the rename box; empty for <see cref="CarNameProblem.None"/>.</summary>
    public static string Describe(CarNameProblem problem) => problem switch
    {
        CarNameProblem.Empty => CarNameStrings.CarNameEmpty,
        CarNameProblem.TooLong => string.Format(CultureInfo.CurrentCulture, CarNameStrings.CarNameTooLong, MaxLength),
        _ => ""
    };

    /// <summary>
    /// Reads the file, replacing what is in memory. A missing file is an empty list; unreadable entries are skipped (and logged). Never
    /// throws for a disk or content problem.
    /// </summary>
    public void Load()
    {
        _names = [];
        _unknownFields = null;
        _writeSuspended = false;

        string json;
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            json = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.CarNamesUnreadable(_logger, ex, _path);
            _writeSuspended = true; // the file is there but unreadable: never overwrite what was not seen
            return;
        }

        try
        {
            // Locals first, fields only once the whole file parsed: a file that fails half-way (say a "cars" that is not an object) must
            // not leave its unknown top-level fields behind to be written into the next save.
            var names = Parse(json, out var skipped, out var unknownFields, out var fileVersion);
            _names = names;
            _unknownFields = unknownFields;
            if (fileVersion > SchemaVersion)
            {
                AppLog.CarNamesFromNewerVersion(_logger, fileVersion, SchemaVersion);
            }

            if (skipped > 0)
            {
                AppLog.CarNamesEntriesSkipped(_logger, skipped, _path);
            }

            AppLog.CarNamesLoaded(_logger, _names.Count, _path);
        }
        catch (JsonException ex)
        {
            AppLog.CarNamesCorrupt(_logger, ex, _path);
            if (!AtomicFile.KeepCorruptCopy(_path))
            {
                AppLog.CorruptCopyFailed(_logger, _path);
                _writeSuspended = true; // the damaged file has no copy: replacing it would destroy the only evidence (same rule as the settings)
            }
        }
    }

    /// <summary>
    /// Gives a car model a nickname (or replaces it), saves, and tells listeners. Nothing changes when the name is not acceptable.
    /// </summary>
    /// <param name="carOrdinal">The game's id of the car model; must be positive (0 means "no car yet").</param>
    /// <param name="rawName">The name as typed; it is cleaned up as <see cref="Validate"/> describes.</param>
    /// <returns><see cref="CarNameProblem.None"/> when the name was stored.</returns>
    public CarNameProblem TrySet(int carOrdinal, string? rawName)
    {
        var problem = Validate(rawName, out var name);
        if (problem != CarNameProblem.None || carOrdinal <= 0)
        {
            return problem;
        }

        if (_names.TryGetValue(carOrdinal, out var existing) && existing == name)
        {
            return CarNameProblem.None; // nothing to write
        }

        _names[carOrdinal] = name;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return CarNameProblem.None;
    }

    /// <summary>Forgets a car's nickname, so it shows as "Car #1234" again. Returns false when it had none.</summary>
    public bool Remove(int carOrdinal)
    {
        if (!_names.Remove(carOrdinal))
        {
            return false;
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Save()
    {
        if (_writeSuspended)
        {
            AppLog.CarNamesWriteSuspended(_logger, _path);
            return;
        }

        try
        {
            var cars = new JsonObject();
            foreach (var (ordinal, name) in _names.OrderBy(pair => pair.Key))
            {
                cars[ordinal.ToString(CultureInfo.InvariantCulture)] = name;
            }

            var root = new JsonObject { ["schemaVersion"] = SchemaVersion, ["cars"] = cars };
            if (_unknownFields is { } extra)
            {
                // After the known fields, so a known name always wins over an unknown one of the same name.
                foreach (var (name, value) in extra)
                {
                    root.TryAdd(name, JsonNode.Parse(value.GetRawText()));
                }
            }

            AtomicFile.WriteAllText(_path, root.ToJsonString(WriteOptions));
            AppLog.CarNamesSaved(_logger, _names.Count, _path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.CarNamesSaveFailed(_logger, ex, _path);
        }
    }

    private static Dictionary<int, string> Parse(
        string json, out int skipped, out Dictionary<string, JsonElement>? unknownFields, out int fileVersion)
    {
        skipped = 0;
        unknownFields = null;
        fileVersion = SchemaVersion;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The car names file does not contain an object.");
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.NameEquals("schemaVersion"))
            {
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var version) && version > 0)
                {
                    fileVersion = version;
                }
            }
            else if (!property.NameEquals("cars"))
            {
                // Clone: the element points into the document, which is disposed when this method returns.
                (unknownFields ??= [])[property.Name] = property.Value.Clone();
            }
        }

        var names = new Dictionary<int, string>();
        if (!document.RootElement.TryGetProperty("cars", out var cars))
        {
            return names; // a file from some other layout: nothing usable, nothing to complain about
        }

        if (cars.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The 'cars' value is not an object.");
        }

        foreach (var entry in cars.EnumerateObject())
        {
            // An entry is usable only as a positive whole ordinal and a name that passes the same rules as typing one. The loader
            // trims like Validate does but a name that is too long is skipped, not cut: the player never typed a cut name.
            if (int.TryParse(entry.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal)
                && ordinal > 0
                && entry.Value.ValueKind == JsonValueKind.String
                && Validate(entry.Value.GetString(), out var name) == CarNameProblem.None)
            {
                names[ordinal] = name;
            }
            else
            {
                skipped++;
            }
        }

        return names;
    }
}
