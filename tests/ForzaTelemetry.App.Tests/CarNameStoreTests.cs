using System.IO;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.App.Tests;

/// <summary>The car nickname file: validation, atomic save, tolerant read. Every test uses its own temp folder, never the real %APPDATA%.</summary>
public sealed class CarNameStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ForzaTelemetryTests", Guid.NewGuid().ToString("N"));

    private string CarsPath => Path.Combine(_directory, "cars.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // ---- Validation ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Daily GT", "Daily GT")]
    [InlineData("  Daily GT  ", "Daily GT")]
    [InlineData("A", "A")]
    [InlineData("Ünïcode ñ", "Ünïcode ñ")]
    [InlineData("two\r\nlines", "two  lines")] // a pasted line break becomes spaces, not a hidden character in the file
    [InlineData("tab\there", "tab here")]
    public void A_good_name_is_trimmed_and_cleaned(string raw, string expected)
    {
        Assert.Equal(CarNameProblem.None, CarNameStore.Validate(raw, out var name));
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void Nothing_left_after_trimming_is_empty(string? raw) =>
        Assert.Equal(CarNameProblem.Empty, CarNameStore.Validate(raw, out _));

    [Fact]
    public void Forty_characters_fit_and_forty_one_do_not()
    {
        Assert.Equal(CarNameProblem.None, CarNameStore.Validate(new string('x', CarNameStore.MaxLength), out _));
        Assert.Equal(CarNameProblem.TooLong, CarNameStore.Validate(new string('x', CarNameStore.MaxLength + 1), out _));
    }

    [Fact]
    public void The_limit_counts_the_trimmed_name_not_the_padding() =>
        Assert.Equal(CarNameProblem.None, CarNameStore.Validate("  " + new string('x', CarNameStore.MaxLength) + "  ", out _));

    [Fact]
    public void Every_problem_has_a_localized_sentence()
    {
        Assert.Equal(CarNameStrings.CarNameEmpty, CarNameStore.Describe(CarNameProblem.Empty));
        Assert.Contains("40", CarNameStore.Describe(CarNameProblem.TooLong));
        Assert.Equal("", CarNameStore.Describe(CarNameProblem.None));
    }

    // ---- Set, find, remove ----------------------------------------------------------------------------------

    [Fact]
    public void A_name_is_found_by_ordinal_and_other_cars_have_none()
    {
        var store = new CarNameStore(CarsPath);

        Assert.Equal(CarNameProblem.None, store.TrySet(2211, "  Daily GT "));

        Assert.Equal("Daily GT", store.Find(2211));
        Assert.Null(store.Find(2212));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void A_refused_name_changes_nothing_and_writes_nothing()
    {
        var store = new CarNameStore(CarsPath);
        store.TrySet(2211, "Daily GT");
        var raised = 0;
        store.Changed += (_, _) => raised++;

        Assert.Equal(CarNameProblem.Empty, store.TrySet(2211, "   "));
        Assert.Equal(CarNameProblem.TooLong, store.TrySet(2211, new string('x', 41)));

        Assert.Equal("Daily GT", store.Find(2211));
        Assert.Equal(0, raised);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void There_is_no_car_to_name_for_a_non_positive_ordinal(int ordinal)
    {
        var store = new CarNameStore(CarsPath);

        store.TrySet(ordinal, "Ghost");

        Assert.Equal(0, store.Count);
        Assert.False(File.Exists(CarsPath));
    }

    [Fact]
    public void Changed_is_raised_for_a_real_change_only()
    {
        var store = new CarNameStore(CarsPath);
        var raised = 0;
        store.Changed += (_, _) => raised++;

        store.TrySet(2211, "Daily GT");
        store.TrySet(2211, "Daily GT"); // the same name again: nothing to write or announce
        store.TrySet(2211, "Track toy");
        Assert.True(store.Remove(2211));
        Assert.False(store.Remove(2211)); // already gone

        Assert.Equal(3, raised);
        Assert.Null(store.Find(2211));
    }

    // ---- Persistence ------------------------------------------------------------------------------------------

    [Fact]
    public void Names_survive_a_restart_in_a_readable_file()
    {
        var store = new CarNameStore(CarsPath);
        store.TrySet(2211, "Daily GT");
        store.TrySet(77, "Ünïcode ñ");

        var reloaded = new CarNameStore(CarsPath);
        reloaded.Load();

        Assert.Equal("Daily GT", reloaded.Find(2211));
        Assert.Equal("Ünïcode ñ", reloaded.Find(77));
        var text = File.ReadAllText(CarsPath);
        Assert.Contains("\"schemaVersion\": 1", text);
        Assert.Contains("\"77\": \"Ünïcode ñ\"", text); // accents as typed, not escapes
        Assert.True(text.IndexOf("\"77\"", StringComparison.Ordinal) < text.IndexOf("\"2211\"", StringComparison.Ordinal)); // stable order
    }

    [Fact]
    public void A_save_leaves_no_temp_file_behind()
    {
        new CarNameStore(CarsPath).TrySet(2211, "Daily GT");

        Assert.Equal([CarsPath], Directory.GetFiles(_directory));
    }

    [Fact]
    public void Removing_the_last_name_leaves_an_empty_valid_file()
    {
        var store = new CarNameStore(CarsPath);
        store.TrySet(2211, "Daily GT");
        store.Remove(2211);

        var reloaded = new CarNameStore(CarsPath);
        reloaded.Load();

        Assert.Equal(0, reloaded.Count);
    }

    [Fact]
    public void A_missing_file_is_an_empty_list_and_loading_never_creates_it()
    {
        var store = new CarNameStore(CarsPath);

        store.Load();

        Assert.Equal(0, store.Count);
        Assert.False(File.Exists(CarsPath));
    }

    [Fact]
    public void Bad_entries_are_skipped_and_the_good_ones_kept()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(CarsPath, """
            { "schemaVersion": 1, "cars": {
                "2211": "Daily GT",
                "abc": "not a number",
                "0": "no car",
                "-4": "negative",
                "55": 12,
                "56": "   ",
                "57": "this name is far too long for a car, over forty characters",
                "88": "  Padded  " } }
            """);
        var logger = new ListLogger<CarNameStore>();

        var store = new CarNameStore(CarsPath, logger);
        store.Load();

        Assert.Equal(2, store.Count);
        Assert.Equal("Daily GT", store.Find(2211));
        Assert.Equal("Padded", store.Find(88));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.StartsWith("Skipped 6 "));
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("{ \"cars\": ")]
    [InlineData("[1, 2, 3]")]
    [InlineData("")]
    [InlineData("{ \"cars\": [1, 2] }")]
    public void A_corrupt_file_gives_an_empty_list_logs_a_warning_and_keeps_a_copy(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(CarsPath, content);
        var logger = new ListLogger<CarNameStore>();

        var store = new CarNameStore(CarsPath, logger);
        store.Load();

        Assert.Equal(0, store.Count);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(content, File.ReadAllText(CarsPath + ".corrupt"));
    }

    [Fact]
    public void A_file_without_a_cars_section_is_simply_empty()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(CarsPath, """{ "schemaVersion": 1, "somethingElse": true }""");
        var logger = new ListLogger<CarNameStore>();

        var store = new CarNameStore(CarsPath, logger);
        store.Load();

        Assert.Equal(0, store.Count);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public void Fields_this_build_does_not_know_survive_a_save_and_the_version_written_is_this_builds()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(CarsPath, """
            { "schemaVersion": 7, "cars": { "2211": "Daily GT" },
              "garages": [ { "name": "Home", "slots": 3 } ], "lastBackup": "2027-01-02" }
            """);
        var logger = new ListLogger<CarNameStore>();
        var store = new CarNameStore(CarsPath, logger);
        store.Load();

        store.TrySet(77, "Track toy");

        using var written = System.Text.Json.JsonDocument.Parse(File.ReadAllText(CarsPath));
        var root = written.RootElement;
        Assert.Equal(CarNameStore.SchemaVersion, root.GetProperty("schemaVersion").GetInt32()); // never the newer number
        Assert.Equal("Daily GT", root.GetProperty("cars").GetProperty("2211").GetString());
        Assert.Equal("Track toy", root.GetProperty("cars").GetProperty("77").GetString());
        Assert.Equal("Home", root.GetProperty("garages")[0].GetProperty("name").GetString());
        Assert.Equal("2027-01-02", root.GetProperty("lastBackup").GetString());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("newer version"));
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_never_written_over()
    {
        Directory.CreateDirectory(_directory);
        const string original = """{ "schemaVersion": 1, "cars": { "2211": "Daily GT" } }""";
        File.WriteAllText(CarsPath, original);
        var logger = new ListLogger<CarNameStore>();
        var store = new CarNameStore(CarsPath, logger);

        using (new FileStream(CarsPath, FileMode.Open, FileAccess.Read, FileShare.None)) // another process holds it
        {
            store.Load();
        }

        var problem = store.TrySet(77, "Track toy");

        Assert.Equal(CarNameProblem.None, problem);
        Assert.Equal("Track toy", store.Find(77)); // the name still works for this session
        Assert.Equal(original, File.ReadAllText(CarsPath)); // and the real file is exactly as it was
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not written"));
    }

    [Fact]
    public void A_failed_save_is_logged_and_the_name_still_applies_for_this_run()
    {
        // The "folder" the file should live in is occupied by a file, so the save cannot succeed.
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "x");
        var logger = new ListLogger<CarNameStore>();
        var store = new CarNameStore(Path.Combine(blocker, "cars.json"), logger);
        var raised = 0;
        store.Changed += (_, _) => raised++;

        var problem = store.TrySet(2211, "Daily GT");

        Assert.Equal(CarNameProblem.None, problem);
        Assert.Equal("Daily GT", store.Find(2211));
        Assert.Equal(1, raised);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Exception is not null);
    }
}
