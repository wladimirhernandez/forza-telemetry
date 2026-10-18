using ForzaTelemetry.App.Services;
using Microsoft.Extensions.Time.Testing;

namespace ForzaTelemetry.App.Tests;

/// <summary>The recordings folder: naming, listing (newest first, off the UI thread), rename and delete, on a real temp folder.</summary>
public sealed class RecordingLibraryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ForzaTelemetryTests", Guid.NewGuid().ToString("N"), "recordings");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 18, 1, 23, 45, TimeSpan.Zero));

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_folder)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private RecordingLibrary NewLibrary() => new(_folder, _time);

    // ---- Naming -------------------------------------------------------------------------------------------

    [Fact]
    public void A_new_recording_is_named_after_its_local_start_time_and_the_folder_is_created_on_demand()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test"));
        var library = NewLibrary();
        Assert.False(Directory.Exists(_folder)); // nothing is created until needed

        var path = library.NextRecordingPath();

        Assert.True(Directory.Exists(_folder));
        Assert.Equal(_folder, Path.GetDirectoryName(path));
        Assert.Equal("2026-10-18_03-23-45.fh6rec", Path.GetFileName(path)); // 01:23:45 UTC is 03:23:45 local
    }

    [Fact]
    public void A_name_that_is_taken_gets_a_suffix_instead_of_being_overwritten()
    {
        var library = NewLibrary();
        var first = library.NextRecordingPath();
        File.WriteAllText(first, "irreplaceable");

        var second = library.NextRecordingPath();
        File.WriteAllText(second, "x");
        var third = library.NextRecordingPath();

        Assert.NotEqual(first, second);
        Assert.EndsWith("-2.fh6rec", second);
        Assert.EndsWith("-3.fh6rec", third);
        Assert.Equal("irreplaceable", File.ReadAllText(first));
    }

    [Fact]
    public void The_default_folder_is_under_the_roaming_app_data()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ForzaTelemetry", "recordings");

        Assert.Equal(expected, RecordingLibrary.DefaultFolder);
    }

    // ---- Listing ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_missing_folder_is_an_empty_library()
    {
        var items = await NewLibrary().ScanAsync();

        Assert.Empty(items);
    }

    [Fact]
    public async Task Recordings_are_listed_newest_first_with_their_duration_size_and_cars()
    {
        await RecordingFiles.WriteAsync(_folder, "older", packets: 5, intervalMs: 100, car: 111, startedAt: new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
        await RecordingFiles.WriteAsync(_folder, "newer", packets: 11, intervalMs: 100, car: 222, startedAt: new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero));

        var items = await NewLibrary().ScanAsync();

        Assert.Equal(["newer", "older"], items.Select(i => i.Name));
        var newer = items[0];
        Assert.True(newer.IsReadable);
        Assert.Equal(TimeSpan.FromSeconds(1), newer.Duration); // 11 records, 100 ms apart: 0 .. 1000 ms
        Assert.Equal(11, newer.PacketCount);
        Assert.Equal([222], newer.CarOrdinals);
        Assert.Equal(new FileInfo(newer.Path).Length, newer.SizeBytes);
        Assert.False(newer.IsTruncated);
    }

    [Fact]
    public async Task A_file_that_is_not_a_recording_is_listed_as_unreadable_not_hidden()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(Path.Combine(_folder, "junk.fh6rec"), "this is not a recording at all");
        await RecordingFiles.WriteAsync(_folder, "real");

        var items = await NewLibrary().ScanAsync();

        Assert.Equal(2, items.Count);
        var junk = items.Single(i => i.Name == "junk");
        Assert.False(junk.IsReadable);
        Assert.True(items.Single(i => i.Name == "real").IsReadable);
    }

    [Fact]
    public async Task A_half_written_last_record_marks_the_recording_truncated_but_still_readable()
    {
        var path = await RecordingFiles.WriteAsync(_folder, "crashed", packets: 6);
        var bytes = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, bytes[..^50]); // the app died in the middle of the last record

        var item = Assert.Single(await NewLibrary().ScanAsync());

        Assert.True(item.IsReadable);
        Assert.True(item.IsTruncated);
        Assert.Equal(5, item.PacketCount);
    }

    [Fact]
    public async Task The_file_being_recorded_is_left_out_of_the_list()
    {
        var growing = await RecordingFiles.WriteAsync(_folder, "recording-now");
        await RecordingFiles.WriteAsync(_folder, "finished");

        var items = await NewLibrary().ScanAsync(excludePath: growing);

        Assert.Equal(["finished"], items.Select(i => i.Name));
    }

    [Fact]
    public async Task Other_files_in_the_folder_are_ignored()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(Path.Combine(_folder, "notes.txt"), "hello");
        await RecordingFiles.WriteAsync(_folder, "real");

        var items = await NewLibrary().ScanAsync();

        Assert.Equal(["real"], items.Select(i => i.Name));
    }

    [Fact]
    public async Task A_rewritten_file_is_scanned_again_not_served_from_the_cache()
    {
        var library = NewLibrary();
        var path = await RecordingFiles.WriteAsync(_folder, "grows", packets: 3);
        Assert.Equal(3, (await library.ScanAsync()).Single().PacketCount);

        await File.WriteAllBytesAsync(path, await RecordingFiles.BytesAsync(packets: 8));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1)); // a coarse file-system clock must not hide the change

        Assert.Equal(8, (await library.ScanAsync()).Single().PacketCount);
    }

    // ---- Rename -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rename_keeps_the_extension_and_moves_the_file()
    {
        var path = await RecordingFiles.WriteAsync(_folder, "old");

        var result = NewLibrary().TryRename(path, "Monza qualifying", out var newPath);

        Assert.Equal(RecordingRenameResult.Renamed, result);
        Assert.Equal(Path.Combine(_folder, "Monza qualifying.fh6rec"), newPath);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(newPath));
    }

    [Fact]
    public async Task A_typed_extension_is_not_doubled()
    {
        var path = await RecordingFiles.WriteAsync(_folder, "old");

        NewLibrary().TryRename(path, "fresh.fh6rec", out var newPath);

        Assert.Equal(Path.Combine(_folder, "fresh.fh6rec"), newPath);
    }

    [Fact]
    public async Task Rename_never_overwrites_another_recording()
    {
        var first = await RecordingFiles.WriteAsync(_folder, "first", packets: 3);
        var second = await RecordingFiles.WriteAsync(_folder, "second", packets: 7);

        var result = NewLibrary().TryRename(first, "second", out _);

        Assert.Equal(RecordingRenameResult.AlreadyExists, result);
        Assert.True(File.Exists(first));
        Assert.Equal(await RecordingFiles.BytesAsync(packets: 7), await File.ReadAllBytesAsync(second));
    }

    [Fact]
    public async Task Renaming_to_the_same_name_or_only_changing_its_case_is_not_an_error()
    {
        var path = await RecordingFiles.WriteAsync(_folder, "monza");
        var library = NewLibrary();

        Assert.Equal(RecordingRenameResult.Renamed, library.TryRename(path, "monza", out _));
        Assert.Equal(RecordingRenameResult.Renamed, library.TryRename(path, "Monza", out var cased));
        Assert.Equal("Monza.fh6rec", Path.GetFileName(Directory.GetFiles(_folder).Single()));
        Assert.Equal(Path.Combine(_folder, "Monza.fh6rec"), cased);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("what?")]
    [InlineData("CON")]
    public async Task Rename_refuses_names_windows_would_not_accept(string name)
    {
        var path = await RecordingFiles.WriteAsync(_folder, "keep");

        var result = NewLibrary().TryRename(path, name, out var newPath);

        Assert.Equal(RecordingRenameResult.InvalidName, result);
        Assert.Equal(path, newPath);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Rename_reports_a_file_that_is_in_use_instead_of_throwing()
    {
        var path = await RecordingFiles.WriteAsync(_folder, "open");
        using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); // no FileShare.Delete: the move fails

        var result = NewLibrary().TryRename(path, "renamed", out _);

        Assert.Equal(RecordingRenameResult.Failed, result);
        Assert.True(File.Exists(path));
    }

    // ---- Delete -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_removes_the_file_and_it_leaves_the_list()
    {
        var library = NewLibrary();
        var path = await RecordingFiles.WriteAsync(_folder, "doomed");
        await RecordingFiles.WriteAsync(_folder, "kept");
        Assert.Equal(2, (await library.ScanAsync()).Count);

        Assert.True(library.TryDelete(path));

        Assert.False(File.Exists(path));
        Assert.Equal(["kept"], (await library.ScanAsync()).Select(i => i.Name));
    }

    [Fact]
    public async Task Delete_of_a_file_in_use_reports_failure()
    {
        var path = await RecordingFiles.WriteAsync(_folder, "open");
        using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.False(NewLibrary().TryDelete(path));
        Assert.True(File.Exists(path));
    }
}

/// <summary>What a typed recording name must satisfy.</summary>
public class RecordingNameValidatorTests
{
    [Theory]
    [InlineData("2026-10-18_01-23-45")]
    [InlineData("Monza qualifying")]
    [InlineData("a.b")]
    [InlineData("coche n.º 12")]
    public void Ordinary_names_are_accepted(string name) =>
        Assert.Equal(RecordingNameError.None, RecordingNameValidator.Validate(name));

    [Theory]
    [InlineData(null, RecordingNameError.Empty)]
    [InlineData("", RecordingNameError.Empty)]
    [InlineData("  ", RecordingNameError.Empty)]
    [InlineData("a\\b", RecordingNameError.InvalidCharacters)]
    [InlineData("a|b", RecordingNameError.InvalidCharacters)]
    [InlineData("a\"b", RecordingNameError.InvalidCharacters)]
    [InlineData("trailing.", RecordingNameError.TrailingDotOrSpace)]
    [InlineData("trailing ", RecordingNameError.TrailingDotOrSpace)]
    [InlineData(" leading", RecordingNameError.TrailingDotOrSpace)]
    [InlineData("con", RecordingNameError.ReservedName)]
    [InlineData("NUL.backup", RecordingNameError.ReservedName)]
    [InlineData("com1", RecordingNameError.ReservedName)]
    public void Names_windows_would_mangle_or_refuse_are_named_for_what_is_wrong(string? name, RecordingNameError expected) =>
        Assert.Equal(expected, RecordingNameValidator.Validate(name));

    [Fact]
    public void A_name_over_the_limit_is_too_long() =>
        Assert.Equal(RecordingNameError.TooLong, RecordingNameValidator.Validate(new string('a', RecordingNameValidator.MaxLength + 1)));

    [Fact]
    public void The_longest_allowed_name_is_accepted() =>
        Assert.Equal(RecordingNameError.None, RecordingNameValidator.Validate(new string('a', RecordingNameValidator.MaxLength)));

    [Theory]
    [InlineData("  name  ", "name")]
    [InlineData("name.fh6rec", "name")]
    [InlineData("NAME.FH6REC", "NAME")]
    [InlineData("name.fh6rec.fh6rec", "name.fh6rec")]
    [InlineData(null, "")]
    public void Normalize_trims_and_drops_one_typed_extension(string? typed, string expected) =>
        Assert.Equal(expected, RecordingNameValidator.Normalize(typed));
}
