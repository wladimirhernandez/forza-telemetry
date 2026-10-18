using System.IO;
using System.Text.Json;
using ForzaTelemetry.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ForzaTelemetry.App.Tests;

/// <summary>Merging, round trips, tolerance and failure handling of the per-car performance file. Every test uses its own temp folder, never the real %APPDATA%.</summary>
public sealed class PerformanceHistoryStoreTests : IDisposable
{
    private static readonly CarKey Gt = new(1234, 780);
    private static readonly CarKey Hatch = new(77, 450);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ForzaTelemetryTests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));

    private string HistoryPath => Path.Combine(_directory, "performance.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private PerformanceHistoryStore NewStore(ILogger? logger = null) => new(HistoryPath, _time, logger);

    private static PerformanceResult Run(double? t100 = null, double? t200 = null, double? quarter = null, double? trap = null, double? top = null) =>
        new(t100, t200, quarter, trap, top);

    // ---- Merge --------------------------------------------------------------------------------------

    [Fact]
    public void A_first_result_creates_the_record_but_is_not_an_improvement()
    {
        var store = NewStore();

        var improved = store.Merge(Gt, 3, 2, Run(t100: 3.2, quarter: 11.9, trap: 190, top: 250));

        Assert.False(improved.Any); // nothing was beaten: it is the first value, not a new best over an old one
        var record = Assert.Single(store.Records);
        Assert.Equal(Gt, record.Car);
        Assert.Equal(3.2, record.ZeroTo100Seconds);
        Assert.Null(record.ZeroTo200Seconds);
        Assert.Equal(11.9, record.QuarterMileSeconds);
        Assert.Equal(190, record.QuarterMileTrapKmh);
        Assert.Equal(250, record.TopSpeedKmh);
        Assert.Equal(_time.GetUtcNow(), record.UpdatedAt);
    }

    [Fact]
    public void A_better_value_replaces_the_old_one_and_is_reported_as_an_improvement()
    {
        var store = NewStore();
        store.Merge(Gt, 3, 2, Run(t100: 3.2, top: 250));
        _time.Advance(TimeSpan.FromMinutes(5));

        var improved = store.Merge(Gt, 3, 2, Run(t100: 3.0, top: 240));

        Assert.True(improved.ZeroTo100);
        Assert.False(improved.TopSpeed); // 240 < 250: not better
        var record = store.Find(Gt)!;
        Assert.Equal(3.0, record.ZeroTo100Seconds);
        Assert.Equal(250, record.TopSpeedKmh);
        Assert.Equal(_time.GetUtcNow(), record.UpdatedAt); // when a value last improved
    }

    [Fact]
    public void A_worse_or_equal_run_changes_nothing_raises_nothing_and_needs_no_write()
    {
        var store = NewStore();
        store.Merge(Gt, 3, 2, Run(t100: 3.0, t200: 9.0, quarter: 11.5, trap: 200, top: 260));
        store.Flush();
        var changed = 0;
        store.Changed += (_, _) => changed++;
        var before = store.Find(Gt);

        var improved = store.Merge(Gt, 3, 2, Run(t100: 3.0, t200: 9.5, quarter: 11.6, trap: 210, top: 255));

        Assert.False(improved.Any);
        Assert.Equal(before, store.Find(Gt));
        Assert.Equal(0, changed);
        File.Delete(HistoryPath);
        Assert.True(store.Flush()); // not dirty: nothing to write
        Assert.False(File.Exists(HistoryPath));
    }

    [Fact]
    public void The_trap_speed_belongs_to_the_best_quarter_mile_not_to_the_fastest_line_speed()
    {
        var store = NewStore();
        store.Merge(Gt, 3, 2, Run(quarter: 11.5, trap: 190));

        store.Merge(Gt, 3, 2, Run(quarter: 11.9, trap: 215)); // slower run, higher line speed: not the best run

        Assert.Equal(190, store.Find(Gt)!.QuarterMileTrapKmh);

        store.Merge(Gt, 3, 2, Run(quarter: 11.2, trap: 185)); // new best run: its trap speed comes with it

        Assert.Equal(185, store.Find(Gt)!.QuarterMileTrapKmh);
    }

    [Fact]
    public void Partial_runs_fill_in_only_what_they_measured()
    {
        var store = NewStore();
        store.Merge(Gt, 3, 2, Run(t100: 3.4));

        store.Merge(Gt, 3, 2, Run(t200: 10.2, top: 244));

        var record = store.Find(Gt)!;
        Assert.Equal(3.4, record.ZeroTo100Seconds);
        Assert.Equal(10.2, record.ZeroTo200Seconds);
        Assert.Equal(244, record.TopSpeedKmh);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Unusable_numbers_are_never_stored(double bad)
    {
        var store = NewStore();

        store.Merge(Gt, 3, 2, Run(t100: bad, top: 200));
        store.Merge(Gt, 3, 2, Run(t100: bad));

        Assert.Null(store.Find(Gt)!.ZeroTo100Seconds);
    }

    [Fact]
    public void A_run_with_nothing_usable_creates_no_record()
    {
        var store = NewStore();

        store.Merge(Gt, 3, 2, Run());

        Assert.Empty(store.Records);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(1000)]
    public void A_car_without_a_real_performance_index_is_ignored(int performanceIndex)
    {
        var store = NewStore();

        store.Merge(new CarKey(1234, performanceIndex), 3, 2, Run(t100: 3.0));

        Assert.Empty(store.Records);
    }

    [Fact]
    public void The_same_car_at_another_performance_index_is_a_separate_record()
    {
        var store = NewStore();

        store.Merge(new CarKey(1234, 700), 2, 2, Run(t100: 4.0));
        store.Merge(new CarKey(1234, 780), 3, 2, Run(t100: 3.0));

        Assert.Equal(2, store.Records.Count);
    }

    [Fact]
    public void Changed_is_raised_only_when_something_was_stored()
    {
        var store = NewStore();
        var changed = 0;
        store.Changed += (_, _) => changed++;

        store.Merge(Gt, 3, 2, Run());
        Assert.Equal(0, changed);

        store.Merge(Gt, 3, 2, Run(t100: 3.0));
        Assert.Equal(1, changed);
    }

    // ---- Persistence ----------------------------------------------------------------------------------

    [Fact]
    public void Flush_then_Load_round_trips_every_field()
    {
        var store = NewStore();
        store.Merge(Gt, 3, 2, Run(3.1, 9.4, 11.8, 192.5, 251.2));
        store.Merge(Hatch, 1, 0, Run(t100: 7.0));
        Assert.True(store.Flush());

        var loaded = NewStore();
        loaded.Load();

        Assert.Equal(store.Records.OrderBy(r => r.Car.CarOrdinal), loaded.Records.OrderBy(r => r.Car.CarOrdinal));
    }

    [Fact]
    public void The_file_is_readable_json_with_a_schema_version_and_camelCase_names()
    {
        var store = NewStore();
        store.Merge(Gt, 3, 2, Run(t100: 3.1));
        store.Flush();

        using var document = JsonDocument.Parse(File.ReadAllText(HistoryPath));
        var root = document.RootElement;

        Assert.Equal(PerformanceHistoryStore.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        var car = root.GetProperty("cars")[0];
        Assert.Equal(1234, car.GetProperty("carOrdinal").GetInt32());
        Assert.Equal(780, car.GetProperty("performanceIndex").GetInt32());
        Assert.Equal(3.1, car.GetProperty("zeroTo100Seconds").GetDouble());
    }

    [Fact]
    public void Flush_writes_only_when_something_changed_and_leaves_no_temp_file()
    {
        var store = NewStore();
        Assert.True(store.Flush());
        Assert.False(File.Exists(HistoryPath)); // nothing to say yet

        store.Merge(Gt, 3, 2, Run(t100: 3.1));
        Assert.True(store.Flush());

        Assert.True(File.Exists(HistoryPath));
        Assert.False(File.Exists(HistoryPath + ".tmp"));
    }

    [Fact]
    public void A_missing_file_is_an_empty_history()
    {
        var store = NewStore();

        store.Load();

        Assert.Empty(store.Records);
        Assert.False(File.Exists(HistoryPath)); // loading never creates the file
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("{ \"cars\": ")]
    [InlineData("[1, 2]")]
    [InlineData("null")]
    [InlineData("")]
    public void A_corrupt_file_is_an_empty_history_logs_a_warning_and_keeps_a_copy(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(HistoryPath, content);
        var logger = new ListLogger<PerformanceHistoryStore>();
        var store = NewStore(logger);

        store.Load();

        Assert.Empty(store.Records);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("corrupt"));
        Assert.Equal(content, File.ReadAllText(HistoryPath + ".corrupt"));
    }

    [Fact]
    public void Unreadable_entries_are_skipped_and_the_good_ones_survive()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            HistoryPath,
            """
            { "schemaVersion": 1, "cars": [
              { "carOrdinal": 1234, "performanceIndex": 780, "carClass": 3, "drivetrainType": 2, "zeroTo100Seconds": 3.1, "updatedAt": "2026-10-18T12:00:00+00:00" },
              { "carOrdinal": "abc", "performanceIndex": 780, "zeroTo100Seconds": 3.0 },
              { "carOrdinal": 5, "performanceIndex": 0, "zeroTo100Seconds": 3.0 },
              "not an object",
              { "carOrdinal": 77, "performanceIndex": 450, "zeroTo100Seconds": "fast", "zeroTo200Seconds": -4, "topSpeedKmh": 180, "updatedAt": "garbage" }
            ] }
            """);
        var logger = new ListLogger<PerformanceHistoryStore>();
        var store = NewStore(logger);

        store.Load();

        Assert.Equal([77, 1234], store.Records.Select(r => r.Car.CarOrdinal).Order());
        Assert.Equal(3.1, store.Find(Gt)!.ZeroTo100Seconds);
        var hatch = store.Find(Hatch)!;
        Assert.Null(hatch.ZeroTo100Seconds); // a string where a number belongs: no value, but the entry (and its top speed) lives
        Assert.Null(hatch.ZeroTo200Seconds); // negative: unusable
        Assert.Equal(180, hatch.TopSpeedKmh);
        Assert.Equal(DateTimeOffset.UnixEpoch, hatch.UpdatedAt); // unreadable date: unknown, not a crash
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains('3'));
        Assert.False(File.Exists(HistoryPath + ".corrupt")); // the file as a whole was fine
    }

    [Fact]
    public void Property_names_are_read_case_insensitively()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(HistoryPath, """{ "Cars": [ { "CarOrdinal": 1234, "PerformanceINDEX": 780, "ZeroTo100Seconds": 3.1 } ] }""");
        var store = NewStore();

        store.Load();

        Assert.Equal(3.1, store.Find(Gt)!.ZeroTo100Seconds);
    }

    [Fact]
    public void Load_replaces_what_was_in_memory()
    {
        var store = NewStore();
        store.Merge(Gt, 3, 2, Run(t100: 3.1)); // never flushed

        store.Load();

        Assert.Empty(store.Records);
    }

    [Fact]
    public void Clear_forgets_every_record_in_memory_and_on_disk_and_raises_Changed()
    {
        var store = NewStore();
        store.Merge(Gt, 3, 2, Run(t100: 3.1));
        store.Flush();
        var changed = 0;
        store.Changed += (_, _) => changed++;

        store.Clear();

        Assert.Empty(store.Records);
        Assert.Equal(1, changed);
        var reloaded = NewStore();
        reloaded.Load();
        Assert.Empty(reloaded.Records);
    }

    [Fact]
    public void A_failed_write_returns_false_logs_and_is_retried_by_the_next_flush()
    {
        // The history "file" is an existing DIRECTORY, so the swap-in step cannot succeed.
        Directory.CreateDirectory(HistoryPath);
        var logger = new ListLogger<PerformanceHistoryStore>();
        var store = NewStore(logger);
        store.Merge(Gt, 3, 2, Run(t100: 3.1));

        Assert.False(store.Flush());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.False(File.Exists(HistoryPath + ".tmp"));

        Directory.Delete(HistoryPath); // the disk problem goes away
        Assert.True(store.Flush()); // still dirty: this write goes through
        Assert.True(File.Exists(HistoryPath));
    }

    [Fact]
    public void The_default_path_is_performance_json_under_ForzaTelemetry_in_AppData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ForzaTelemetry", "performance.json");

        Assert.Equal(expected, PerformanceHistoryStore.DefaultPath);
    }
}
