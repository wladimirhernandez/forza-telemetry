using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForzaTelemetry.App.Services;

/// <summary>Which car a result belongs to: the game's car id plus its performance index (an upgraded car is a different car for timing purposes).</summary>
/// <param name="CarOrdinal">The game's id of the car model.</param>
/// <param name="PerformanceIndex">The car's PI (100..999); a tune or upgrade changes it, and so changes the times.</param>
public readonly record struct CarKey(int CarOrdinal, int PerformanceIndex)
{
    /// <summary>The documented range of a real performance index; 0 (menus, loading) and anything outside it is not a car.</summary>
    public const int MinPerformanceIndex = 100;

    /// <summary>See <see cref="MinPerformanceIndex"/>.</summary>
    public const int MaxPerformanceIndex = 999;

    /// <summary>True when the pair describes a real car (the PI is in range).</summary>
    public bool IsValid => PerformanceIndex is >= MinPerformanceIndex and <= MaxPerformanceIndex;
}

/// <summary>The best results ever measured for one car. A null value means "no run has reached that line yet".</summary>
/// <param name="Car">The car.</param>
/// <param name="CarClass">The game's class number (see <see cref="CarInfoFormatter.ClassLabel"/>), as last seen for this car.</param>
/// <param name="DrivetrainType">The game's drivetrain number (see <see cref="CarInfoFormatter.DrivetrainLabel"/>).</param>
/// <param name="ZeroTo100Seconds">Best 0-100 km/h time.</param>
/// <param name="ZeroTo200Seconds">Best 0-200 km/h time.</param>
/// <param name="QuarterMileSeconds">Best quarter-mile time.</param>
/// <param name="QuarterMileTrapKmh">Speed at the line of THAT best quarter mile (it belongs to the run, not to the fastest line speed ever).</param>
/// <param name="TopSpeedKmh">Highest speed ever reached.</param>
/// <param name="UpdatedAt">When any of the values last improved.</param>
/// <param name="HundredTo200Seconds">Best rolling 100-200 km/h time.</param>
/// <param name="ZeroTo60MphSeconds">Best 0-60 mph time.</param>
/// <param name="SixtyTo130MphSeconds">Best 60-130 mph time.</param>
/// <param name="BrakeHundredMeters">Shortest 100-0 km/h braking distance, in meters.</param>
/// <param name="BrakeSixtyMphMeters">Shortest 60-0 mph braking distance, in meters.</param>
/// <remarks>
/// The last five values were added after the first release: they default to null, and a file written before them simply has none
/// (see <see cref="PerformanceHistoryStore"/>), so an old history loads unchanged and fills in as the car is driven.
/// </remarks>
public sealed record CarPerformanceRecord(
    CarKey Car,
    int CarClass,
    int DrivetrainType,
    double? ZeroTo100Seconds,
    double? ZeroTo200Seconds,
    double? QuarterMileSeconds,
    double? QuarterMileTrapKmh,
    double? TopSpeedKmh,
    DateTimeOffset UpdatedAt,
    double? HundredTo200Seconds = null,
    double? ZeroTo60MphSeconds = null,
    double? SixtyTo130MphSeconds = null,
    double? BrakeHundredMeters = null,
    double? BrakeSixtyMphMeters = null);

/// <summary>One run's measurements to fold into the history; null means "not measured in this run".</summary>
/// <remarks>The last five values default to null so a caller that only knows the original five keeps working.</remarks>
public sealed record PerformanceResult(
    double? ZeroTo100Seconds,
    double? ZeroTo200Seconds,
    double? QuarterMileSeconds,
    double? QuarterMileTrapKmh,
    double? TopSpeedKmh,
    double? HundredTo200Seconds = null,
    double? ZeroTo60MphSeconds = null,
    double? SixtyTo130MphSeconds = null,
    double? BrakeHundredMeters = null,
    double? BrakeSixtyMphMeters = null);

/// <summary>Which values of a merge beat a PREVIOUS best (a first-ever value is a record but not an "improvement").</summary>
public readonly record struct PerformanceImprovements(
    bool ZeroTo100,
    bool ZeroTo200,
    bool QuarterMile,
    bool TopSpeed,
    bool HundredTo200 = false,
    bool ZeroTo60Mph = false,
    bool SixtyTo130Mph = false,
    bool BrakeHundred = false,
    bool BrakeSixtyMph = false)
{
    /// <summary>True when at least one value beat an earlier one.</summary>
    public bool Any =>
        ZeroTo100 || ZeroTo200 || QuarterMile || TopSpeed || HundredTo200 || ZeroTo60Mph || SixtyTo130Mph || BrakeHundred || BrakeSixtyMph;
}

/// <summary>
/// The best acceleration windows (0-100, 0-200, 100-200 km/h, 0-60, 60-130 mph), quarter-mile, top-speed and braking
/// (100-0 km/h, 60-0 mph) results per car, kept in memory and in <c>%APPDATA%\ForzaTelemetry\performance.json</c>.
/// </summary>
/// <remarks>
/// <b>Why a file in Roaming AppData, written atomically.</b> Same reasoning as the settings file (see
/// <see cref="JsonSettingsStore"/> and <see cref="AtomicFile"/>): a small per-user file that must survive an interrupted
/// write. A reading that cannot be trusted is skipped, never fatal: a damaged or hand-edited file costs only the bad
/// entries, and a file that is not JSON at all is kept next to the original as <c>.corrupt</c> and replaced by an empty
/// history on the next save.
/// <para>
/// <b>Merging, not recording.</b> <see cref="Merge"/> folds one run into the stored bests value by value (lower time
/// wins, higher speed wins), so calling it twice with the same numbers changes nothing. The quarter-mile TRAP speed travels
/// with the quarter-mile time it was measured on. A time or a braking DISTANCE is better when lower; a speed when higher.
/// </para>
/// <para>
/// <b>Older files.</b> The newer values (the extra windows and the braking distances) are optional properties of each car entry
/// and the layout version stays 1 because nothing that existed changed: a file written before them loads with those values empty,
/// and each fills in the first time it is measured. Entries the file carries for fields this build does not know are not kept.
/// </para>
/// <para>
/// <b>Writes are explicit.</b> <see cref="Merge"/> only updates memory and marks the history dirty; <see cref="Flush"/>
/// writes the file when something changed. The caller decides WHEN (a finished run, a car change, stopping, exiting), so a
/// value that improves 20 times a second while a car accelerates does not become 20 disk writes.
/// </para>
/// <para>
/// Not thread-safe: like the view models, it is used from the UI thread only (the engines that feed it run elsewhere and
/// are read through their own snapshots).
/// </para>
/// </remarks>
public sealed class PerformanceHistoryStore
{
    /// <summary>The layout version of the file this build reads and writes.</summary>
    public const int SchemaVersion = 1;

    // Indented because the file is meant to be readable. The relaxed encoder keeps "+" and accents as typed instead of
    // +-style escapes (the default is safe for HTML, which this local file is not).
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private Dictionary<CarKey, CarPerformanceRecord> _records = [];
    private bool _dirty;

    /// <param name="path">Full path of the history file.</param>
    /// <param name="time">Source of the "updated at" timestamp; injectable for tests.</param>
    /// <param name="logger">Receives load/save outcomes; optional.</param>
    public PerformanceHistoryStore(string path, TimeProvider? time = null, ILogger? logger = null)
    {
        _path = path;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary><c>%APPDATA%\ForzaTelemetry\performance.json</c>.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ForzaTelemetry", "performance.json");

    /// <summary>Raised after the stored values changed (a merge that improved something, or <see cref="Clear"/>).</summary>
    public event EventHandler? Changed;

    /// <summary>The records, in no particular order (callers sort for display). A fresh array, safe to keep.</summary>
    public IReadOnlyList<CarPerformanceRecord> Records => [.. _records.Values];

    /// <summary>The record of one car, or null when it has none.</summary>
    public CarPerformanceRecord? Find(CarKey car) => _records.GetValueOrDefault(car);

    /// <summary>
    /// Reads the file, replacing what is in memory. A missing file is an empty history; unreadable entries are skipped
    /// (and logged). Never throws for a disk or content problem.
    /// </summary>
    public void Load()
    {
        _records = [];
        _dirty = false;

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
            AppLog.PerformanceHistoryUnreadable(_logger, ex, _path);
            return;
        }

        try
        {
            _records = Parse(json, out var skipped);
            if (skipped > 0)
            {
                AppLog.PerformanceHistoryEntriesSkipped(_logger, skipped, _path);
            }

            AppLog.PerformanceHistoryLoaded(_logger, _records.Count, _path);
        }
        catch (JsonException ex)
        {
            AppLog.PerformanceHistoryCorrupt(_logger, ex, _path);
            if (!AtomicFile.KeepCorruptCopy(_path))
            {
                // The copy is best effort, but the log line above promised one: say when it did not happen.
                AppLog.CorruptCopyFailed(_logger, _path);
            }

        }
    }

    /// <summary>
    /// Folds one run into the car's bests (see the class remarks) and reports which values beat an earlier best. Only memory
    /// changes; call <see cref="Flush"/> to persist.
    /// </summary>
    /// <param name="car">The car the run was driven in; an invalid key (no real PI) is ignored.</param>
    /// <param name="carClass">The game's class number for the car.</param>
    /// <param name="drivetrainType">The game's drivetrain number for the car.</param>
    /// <param name="result">What the run measured.</param>
    public PerformanceImprovements Merge(CarKey car, int carClass, int drivetrainType, PerformanceResult result)
    {
        if (!car.IsValid)
        {
            return default;
        }

        var existing = _records.GetValueOrDefault(car);
        var improved100 = Beats(result.ZeroTo100Seconds, existing?.ZeroTo100Seconds, lowerIsBetter: true);
        var improved200 = Beats(result.ZeroTo200Seconds, existing?.ZeroTo200Seconds, lowerIsBetter: true);
        var improvedQuarter = Beats(result.QuarterMileSeconds, existing?.QuarterMileSeconds, lowerIsBetter: true);
        var improvedTop = Beats(result.TopSpeedKmh, existing?.TopSpeedKmh, lowerIsBetter: false);
        var improved100To200 = Beats(result.HundredTo200Seconds, existing?.HundredTo200Seconds, lowerIsBetter: true);
        var improved60Mph = Beats(result.ZeroTo60MphSeconds, existing?.ZeroTo60MphSeconds, lowerIsBetter: true);
        var improved60To130 = Beats(result.SixtyTo130MphSeconds, existing?.SixtyTo130MphSeconds, lowerIsBetter: true);
        var improvedBrake100 = Beats(result.BrakeHundredMeters, existing?.BrakeHundredMeters, lowerIsBetter: true);
        var improvedBrake60 = Beats(result.BrakeSixtyMphMeters, existing?.BrakeSixtyMphMeters, lowerIsBetter: true);

        // Nothing beat a stored value, or a first merge had nothing usable: no record, no write.
        var anyBetter = improved100.Better || improved200.Better || improvedQuarter.Better || improvedTop.Better
            || improved100To200.Better || improved60Mph.Better || improved60To130.Better
            || improvedBrake100.Better || improvedBrake60.Better;
        if (!anyBetter)
        {
            return default;
        }

        _records[car] = new CarPerformanceRecord(
            car,
            carClass,
            drivetrainType,
            improved100.Better ? result.ZeroTo100Seconds : existing?.ZeroTo100Seconds,
            improved200.Better ? result.ZeroTo200Seconds : existing?.ZeroTo200Seconds,
            improvedQuarter.Better ? result.QuarterMileSeconds : existing?.QuarterMileSeconds,
            improvedQuarter.Better ? result.QuarterMileTrapKmh : existing?.QuarterMileTrapKmh,
            improvedTop.Better ? result.TopSpeedKmh : existing?.TopSpeedKmh,
            _time.GetUtcNow(),
            improved100To200.Better ? result.HundredTo200Seconds : existing?.HundredTo200Seconds,
            improved60Mph.Better ? result.ZeroTo60MphSeconds : existing?.ZeroTo60MphSeconds,
            improved60To130.Better ? result.SixtyTo130MphSeconds : existing?.SixtyTo130MphSeconds,
            improvedBrake100.Better ? result.BrakeHundredMeters : existing?.BrakeHundredMeters,
            improvedBrake60.Better ? result.BrakeSixtyMphMeters : existing?.BrakeSixtyMphMeters);

        _dirty = true;
        Changed?.Invoke(this, EventArgs.Empty);

        // "Improved" means beat a PREVIOUS value: the first time a car reaches a line it is a record, not an improvement.
        return new PerformanceImprovements(
            improved100.Better && improved100.HadPrevious,
            improved200.Better && improved200.HadPrevious,
            improvedQuarter.Better && improvedQuarter.HadPrevious,
            improvedTop.Better && improvedTop.HadPrevious,
            improved100To200.Better && improved100To200.HadPrevious,
            improved60Mph.Better && improved60Mph.HadPrevious,
            improved60To130.Better && improved60To130.HadPrevious,
            improvedBrake100.Better && improvedBrake100.HadPrevious,
            improvedBrake60.Better && improvedBrake60.HadPrevious);
    }

    /// <summary>Writes the file if anything changed since the last write. A failed write is logged and retried by the next flush.</summary>
    /// <returns>True when nothing needed writing or the write succeeded.</returns>
    public bool Flush()
    {
        if (!_dirty)
        {
            return true;
        }

        try
        {
            AtomicFile.WriteAllText(_path, Serialize());
            _dirty = false;
            AppLog.PerformanceHistorySaved(_logger, _records.Count, _path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.PerformanceHistorySaveFailed(_logger, ex, _path);
            return false;
        }
    }

    /// <summary>Forgets every record, in memory and in the file.</summary>
    public void Clear()
    {
        if (_records.Count == 0 && !File.Exists(_path))
        {
            return;
        }

        _records = [];
        _dirty = true;
        Flush();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- Comparison -------------------------------------------------------------------------------------------------

    private readonly record struct Comparison(bool Better, bool HadPrevious);

    private static Comparison Beats(double? candidate, double? current, bool lowerIsBetter)
    {
        if (!IsUsable(candidate))
        {
            return new Comparison(false, current is not null);
        }

        if (current is null)
        {
            return new Comparison(true, false);
        }

        return new Comparison(lowerIsBetter ? candidate < current : candidate > current, true);
    }

    private static bool IsUsable(double? value) => value is { } v && double.IsFinite(v) && v > 0;

    // ---- File format ------------------------------------------------------------------------------------------------

    private string Serialize()
    {
        var cars = new JsonArray();
        foreach (var record in _records.Values.OrderBy(r => r.Car.CarOrdinal).ThenBy(r => r.Car.PerformanceIndex))
        {
            cars.Add(new JsonObject
            {
                ["carOrdinal"] = record.Car.CarOrdinal,
                ["performanceIndex"] = record.Car.PerformanceIndex,
                ["carClass"] = record.CarClass,
                ["drivetrainType"] = record.DrivetrainType,
                ["zeroTo100Seconds"] = record.ZeroTo100Seconds,
                ["zeroTo200Seconds"] = record.ZeroTo200Seconds,
                ["quarterMileSeconds"] = record.QuarterMileSeconds,
                ["quarterMileTrapKmh"] = record.QuarterMileTrapKmh,
                ["topSpeedKmh"] = record.TopSpeedKmh,
                ["hundredTo200Seconds"] = record.HundredTo200Seconds,
                ["zeroTo60MphSeconds"] = record.ZeroTo60MphSeconds,
                ["sixtyTo130MphSeconds"] = record.SixtyTo130MphSeconds,
                ["brakeHundredMeters"] = record.BrakeHundredMeters,
                ["brakeSixtyMphMeters"] = record.BrakeSixtyMphMeters,
                ["updatedAt"] = record.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)
            });
        }

        var root = new JsonObject { ["schemaVersion"] = SchemaVersion, ["cars"] = cars };
        return root.ToJsonString(WriteOptions);
    }

    /// <summary>
    /// Reads the file entry by entry. An entry whose identity (car, PI) is unreadable is skipped; an unreadable value inside
    /// an otherwise good entry is simply "no value". Not JSON, or not an object: <see cref="JsonException"/>.
    /// </summary>
    private static Dictionary<CarKey, CarPerformanceRecord> Parse(string json, out int skipped)
    {
        skipped = 0;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The performance history file does not contain an object.");
        }

        var records = new Dictionary<CarKey, CarPerformanceRecord>();
        if (!TryGetProperty(document.RootElement, "cars", out var cars) || cars.ValueKind != JsonValueKind.Array)
        {
            return records;
        }

        foreach (var entry in cars.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !TryReadInt(entry, "carOrdinal", out var ordinal)
                || !TryReadInt(entry, "performanceIndex", out var performanceIndex)
                || !new CarKey(ordinal, performanceIndex).IsValid)
            {
                skipped++;
                continue;
            }

            var car = new CarKey(ordinal, performanceIndex);
            var record = new CarPerformanceRecord(
                car,
                TryReadInt(entry, "carClass", out var carClass) ? carClass : -1,
                TryReadInt(entry, "drivetrainType", out var drivetrain) ? drivetrain : -1,
                ReadValue(entry, "zeroTo100Seconds"),
                ReadValue(entry, "zeroTo200Seconds"),
                ReadValue(entry, "quarterMileSeconds"),
                ReadValue(entry, "quarterMileTrapKmh"),
                ReadValue(entry, "topSpeedKmh"),
                ReadDate(entry, "updatedAt"),
                ReadValue(entry, "hundredTo200Seconds"),
                ReadValue(entry, "zeroTo60MphSeconds"),
                ReadValue(entry, "sixtyTo130MphSeconds"),
                ReadValue(entry, "brakeHundredMeters"),
                ReadValue(entry, "brakeSixtyMphMeters"));

            // Two entries for the same car (a hand-edited file): the later one wins, like a JSON object with a repeated key.
            records[car] = record;
        }

        return records;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryReadInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return TryGetProperty(element, name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }

    private static double? ReadValue(JsonElement element, string name) =>
        TryGetProperty(element, name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out var value)
            && double.IsFinite(value)
            && value > 0
                ? value
                : null;

    private static DateTimeOffset ReadDate(JsonElement element, string name) =>
        TryGetProperty(element, name, out var property)
            && property.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
                ? value
                : DateTimeOffset.UnixEpoch;
}
