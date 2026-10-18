using System.Globalization;
using System.IO;
using System.Text;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using ForzaTelemetry.Core.Laps;
using ForzaTelemetry.Core.Performance;
using ForzaTelemetry.Core.Recording;
using Microsoft.Extensions.Time.Testing;

namespace ForzaTelemetry.App.Tests;

/// <summary>Answers the Save dialog with a fixed path (or "cancelled") and remembers what it was asked.</summary>
internal sealed class FakeFileSaveDialog(string? answer) : IFileSaveDialog
{
    public string? Answer { get; set; } = answer;

    public List<(string Title, string SuggestedName, string Filter)> Asked { get; } = [];

    public string? PickSavePath(string title, string suggestedFileName, string filter)
    {
        Asked.Add((title, suggestedFileName, filter));
        return Answer;
    }
}

/// <summary>Collects progress reports synchronously (the real <see cref="Progress{T}"/> posts them to another thread).</summary>
internal sealed class SyncProgress(Action<double>? also = null) : IProgress<double>
{
    public List<double> Reports { get; } = [];

    public void Report(double value)
    {
        Reports.Add(value);
        also?.Invoke(value);
    }
}

/// <summary>A temp folder that is removed with the test.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ForzaTelemetryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

/// <summary>The CSV exports: the files themselves (columns, rows, units, atomic writing) and the pages that offer them.</summary>
public class ExportTests
{
    private const string Crlf = "\r\n";

    private static string Decode(byte[] bytes)
    {
        // The reader honors the byte order mark: the text starts at the first column name, not at a stray U+FEFF.
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string[] Lines(string csv) => csv.TrimEnd('\r', '\n').Split(Crlf);

    // ---- Laps ---------------------------------------------------------------------------------------

    [Fact]
    public void Laps_are_written_oldest_first_with_the_gap_to_the_best_lap()
    {
        var stream = new MemoryStream();

        LapsCsv.Write(stream, [new LapRecord(2, 63.456, false), new LapRecord(1, 62.0, true), new LapRecord(3, 61.5, true)]);

        var bytes = stream.ToArray();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]); // the byte order mark: Excel needs it to read accents
        Assert.Equal(
            ["lap,time_s,gap_to_best_s", "1,62.000,0.500", "2,63.456,1.956", "3,61.500,0.000"],
            Lines(Decode(bytes)));
    }

    [Fact]
    public void A_lap_with_an_unknown_time_has_empty_cells_and_never_becomes_the_best()
    {
        var stream = new MemoryStream();

        LapsCsv.Write(stream, [new LapRecord(1, 0, false), new LapRecord(2, 70, true)]);

        Assert.Equal(["lap,time_s,gap_to_best_s", "1,,", "2,70.000,0.000"], Lines(Decode(stream.ToArray())));
    }

    [Fact]
    public void No_laps_is_just_the_header()
    {
        var stream = new MemoryStream();

        LapsCsv.Write(stream, []);

        Assert.Equal("lap,time_s,gap_to_best_s" + Crlf, Decode(stream.ToArray()));
    }

    [Fact]
    public void Lap_numbers_are_not_culture_formatted()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");
            var stream = new MemoryStream();

            LapsCsv.Write(stream, [new LapRecord(1, 62.5, true)]);

            Assert.Equal("1,62.500,0.000", Lines(Decode(stream.ToArray()))[1]); // a dot, never "62,500"
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // ---- Performance history ---------------------------------------------------------------------------

    private static CarPerformanceRecord Record(
        int ordinal, int pi, double? zero100 = null, double? zero60Mph = null, double? top = null, double? brake100 = null,
        DateTimeOffset? updatedAt = null) =>
        new(
            new CarKey(ordinal, pi), CarClass: 4, DrivetrainType: 2, ZeroTo100Seconds: zero100, ZeroTo200Seconds: null,
            QuarterMileSeconds: null, QuarterMileTrapKmh: null, TopSpeedKmh: top,
            UpdatedAt: updatedAt ?? new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero),
            ZeroTo60MphSeconds: zero60Mph, BrakeHundredMeters: brake100);

    [Fact]
    public void Performance_has_every_stored_value_in_columns_that_name_their_unit()
    {
        var stream = new MemoryStream();
        var full = new CarPerformanceRecord(
            new CarKey(2211, 780), CarClass: 4, DrivetrainType: 1,
            ZeroTo100Seconds: 3.456, ZeroTo200Seconds: 10.2, QuarterMileSeconds: 11.123, QuarterMileTrapKmh: 201.55, TopSpeedKmh: 312.04,
            UpdatedAt: new DateTimeOffset(2026, 10, 18, 12, 34, 56, TimeSpan.FromHours(-4)),
            HundredTo200Seconds: 6.789, ZeroTo60MphSeconds: 3.2, SixtyTo130MphSeconds: 8.5, BrakeHundredMeters: 35.46, BrakeSixtyMphMeters: 33.04);

        PerformanceCsv.Write(stream, [full]);

        var lines = Lines(Decode(stream.ToArray()));
        Assert.Equal(
            "car_ordinal,car_name,performance_index,class,drivetrain,zero_to_100_kmh_s,zero_to_200_kmh_s,hundred_to_200_kmh_s,"
            + "zero_to_60_mph_s,sixty_to_130_mph_s,quarter_mile_s,quarter_mile_trap_kmh,top_speed_kmh,brake_100_to_0_kmh_m,"
            + "brake_60_to_0_mph_m,updated_at",
            lines[0]);
        Assert.Equal(
            "2211,,780,S1,RWD,3.456,10.200,6.789,3.200,8.500,11.123,201.6,312.0,35.5,33.0,2026-10-18T12:34:56.0000000-04:00",
            lines[1]);
    }

    [Fact]
    public void Performance_leaves_unmeasured_values_empty_and_sorts_by_car_then_performance_index()
    {
        var stream = new MemoryStream();

        PerformanceCsv.Write(stream, [Record(9, 700, zero100: 4.0), Record(2, 800, top: 250), Record(2, 700, brake100: 40)]);

        var rows = Lines(Decode(stream.ToArray())).Skip(1).ToArray();
        Assert.Equal(["2,,700", "2,,800", "9,,700"], rows.Select(r => string.Join(",", r.Split(",").Take(3))));
        Assert.Equal(3, rows.Length);
        Assert.Equal("40.0", rows[0].Split(",")[13]); // brake_100_to_0_kmh_m: the only value ever measured
        Assert.Equal(["", "", "", "", "", "", "", ""], rows[0].Split(",")[5..13]); // every other value is an empty cell
    }

    [Fact]
    public void Performance_puts_the_players_nickname_in_car_name_and_quotes_it_when_needed()
    {
        var stream = new MemoryStream();
        var names = new Dictionary<int, string> { [2211] = "My \"daily\", fast" };

        PerformanceCsv.Write(stream, [Record(2211, 780), Record(9, 700)], names.GetValueOrDefault);

        var rows = Lines(Decode(stream.ToArray())).Skip(1).ToArray();
        Assert.StartsWith("9,,700,", rows[0]);
        Assert.StartsWith("2211,\"My \"\"daily\"\", fast\",780,", rows[1]);
    }

    [Fact]
    public void A_nickname_that_looks_like_a_formula_is_neutralised()
    {
        var stream = new MemoryStream();
        var names = new Dictionary<int, string> { [1] = "=HYPERLINK(\"http://x\")" };

        PerformanceCsv.Write(stream, [Record(1, 500)], names.GetValueOrDefault);

        Assert.StartsWith("1,\"'=HYPERLINK(", Lines(Decode(stream.ToArray()))[1]);
    }

    [Fact]
    public void An_unknown_update_time_is_an_empty_cell()
    {
        var stream = new MemoryStream();

        PerformanceCsv.Write(stream, [Record(1, 500, zero100: 4, updatedAt: DateTimeOffset.UnixEpoch)]);

        Assert.EndsWith(",", Lines(Decode(stream.ToArray()))[1]);
    }

    // ---- Recording -> CSV ------------------------------------------------------------------------------

    private const string RecordingHeader =
        "time_s,race_on,speed_kmh,rpm,gear,throttle_pct,brake_pct,steer_pct,pos_x,pos_z,distance_m,lap,current_lap_s";

    [Fact]
    public async Task A_recording_becomes_one_row_per_packet()
    {
        var recording = new MemoryStream(await RecordingFiles.BytesAsync(packets: 3, intervalMs: 100));
        var csv = new MemoryStream();

        var result = await RecordingCsv.ExportAsync(recording, csv);

        Assert.Equal(new RecordingCsvResult(3, false), result);
        Assert.Equal(
            [
                RecordingHeader,
                "0.000,1,108.00,4500.0,3,0.0,0.0,0.0,0.00,0.00,0.0,1,0.000",
                "0.100,1,108.00,4501.0,3,0.0,0.0,0.0,2.00,1.00,0.0,1,0.000",
                "0.200,1,108.00,4502.0,3,0.0,0.0,0.0,4.00,2.00,0.0,1,0.000"
            ],
            Lines(Decode(csv.ToArray())));
    }

    [Fact]
    public async Task The_inputs_gear_lap_and_menu_flag_are_converted_to_what_a_spreadsheet_user_reads()
    {
        var datagram = RecordingFiles.Datagram(raceOn: false);
        datagram[315] = 255; // throttle
        datagram[316] = 51; // brake: 20%
        datagram[320] = 0x81; // steer: sbyte -127, full left
        datagram[319] = 11; // neutral
        BitConverter.GetBytes((ushort)4).CopyTo(datagram, 312); // laps completed: 4 -> lap 5
        BitConverter.GetBytes(83.4567f).CopyTo(datagram, 304); // CurrentLap
        var packet = TelemetryParser.Parse(datagram);

        var stream = new MemoryStream();
        await using (var writer = await SessionRecordingWriter.CreateAsync(stream, DateTimeOffset.UnixEpoch, leaveOpen: true))
        {
            await writer.WriteAsync(datagram, TimeSpan.FromMilliseconds(1500));
        }

        stream.Position = 0;
        var csv = new MemoryStream();
        await RecordingCsv.ExportAsync(stream, csv);

        var cells = Lines(Decode(csv.ToArray()))[1].Split(',');
        Assert.Equal("1.500", cells[0]);
        Assert.Equal("0", cells[1]); // a menu packet: kept, flagged
        Assert.Equal("N", cells[4]);
        Assert.Equal("100.0", cells[5]);
        Assert.Equal("20.0", cells[6]);
        Assert.Equal("-100.0", cells[7]);
        Assert.Equal((packet.LapNumber + 1).ToString(CultureInfo.InvariantCulture), cells[11]);
        Assert.Equal(packet.CurrentLap.ToString("0.000", CultureInfo.InvariantCulture), cells[12]);
    }

    [Fact]
    public async Task A_datagram_that_does_not_parse_is_skipped_like_the_replay_does()
    {
        var stream = new MemoryStream();
        await using (var writer = await SessionRecordingWriter.CreateAsync(stream, DateTimeOffset.UnixEpoch, leaveOpen: true))
        {
            await writer.WriteAsync(RecordingFiles.Datagram(sequence: 0), TimeSpan.Zero);
            await writer.WriteAsync(new byte[10], TimeSpan.FromMilliseconds(50));
            await writer.WriteAsync(RecordingFiles.Datagram(sequence: 1), TimeSpan.FromMilliseconds(100));
        }

        stream.Position = 0;
        var csv = new MemoryStream();
        var result = await RecordingCsv.ExportAsync(stream, csv);

        Assert.Equal(2, result.Rows);
        Assert.Equal(3, Lines(Decode(csv.ToArray())).Length); // header and two rows
    }

    [Fact]
    public async Task A_recording_cut_off_mid_record_exports_everything_before_the_cut()
    {
        var bytes = await RecordingFiles.BytesAsync(packets: 4);
        var recording = new MemoryStream(bytes[..^100]); // the last record is incomplete
        var csv = new MemoryStream();

        var result = await RecordingCsv.ExportAsync(recording, csv);

        Assert.Equal(new RecordingCsvResult(3, true), result);
    }

    [Fact]
    public async Task A_recording_with_no_packets_is_just_the_header()
    {
        var recording = new MemoryStream(await RecordingFiles.BytesAsync(packets: 0));
        var csv = new MemoryStream();

        var result = await RecordingCsv.ExportAsync(recording, csv);

        Assert.Equal(0, result.Rows);
        Assert.Equal(RecordingHeader + Crlf, Decode(csv.ToArray()));
    }

    [Fact]
    public async Task A_big_recording_streams_with_increasing_progress_ending_at_one()
    {
        var recording = new MemoryStream(await RecordingFiles.BytesAsync(packets: 5000, intervalMs: 16));
        var csv = new MemoryStream();
        var progress = new SyncProgress();

        var result = await RecordingCsv.ExportAsync(recording, csv, progress);

        Assert.Equal(5000, result.Rows);
        Assert.True(progress.Reports.Count >= 4); // one per 1024 records, plus the final one
        Assert.Equal(progress.Reports.Order(), progress.Reports);
        Assert.All(progress.Reports, value => Assert.InRange(value, 0, 1));
        Assert.Equal(1.0, progress.Reports[^1]);
        Assert.Equal(5001, Lines(Decode(csv.ToArray())).Length); // the header and 5000 rows
    }

    [Fact]
    public async Task Cancelling_stops_the_export()
    {
        var recording = new MemoryStream(await RecordingFiles.BytesAsync(packets: 5000));
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(_ => cts.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RecordingCsv.ExportAsync(recording, new MemoryStream(), progress, cts.Token));
    }

    [Fact]
    public async Task Exporting_a_file_writes_the_destination_and_leaves_no_temporary_file()
    {
        using var folder = new TempFolder();
        var source = await RecordingFiles.WriteAsync(folder.Path, "drive", packets: 5);
        var target = folder.File("drive.csv");

        var result = await RecordingCsv.ExportFileAsync(source, target);

        Assert.Equal(5, result.Rows);
        Assert.Equal(6, Lines(Decode(await File.ReadAllBytesAsync(target))).Length);
        Assert.False(File.Exists(target + ".tmp"));
    }

    [Fact]
    public async Task A_cancelled_file_export_leaves_nothing_and_keeps_the_old_file()
    {
        using var folder = new TempFolder();
        var source = await RecordingFiles.WriteAsync(folder.Path, "drive", packets: 5000);
        var target = folder.File("drive.csv");
        await File.WriteAllTextAsync(target, "old");
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RecordingCsv.ExportFileAsync(source, target, new SyncProgress(_ => cts.Cancel()), cts.Token));

        Assert.Equal("old", await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(target + ".tmp"));
    }

    [Fact]
    public async Task Something_that_is_not_a_recording_fails_cleanly_and_creates_no_file()
    {
        using var folder = new TempFolder();
        var source = folder.File("junk.fh6rec");
        await File.WriteAllTextAsync(source, "this is not a recording at all, just text");
        var target = folder.File("junk.csv");

        await Assert.ThrowsAsync<InvalidRecordingException>(() => RecordingCsv.ExportFileAsync(source, target));

        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".tmp"));
    }

    [Fact]
    public async Task A_recording_that_is_open_for_replay_can_still_be_exported()
    {
        using var folder = new TempFolder();
        var source = await RecordingFiles.WriteAsync(folder.Path, "drive", packets: 3);
        await using var replayHandle = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var result = await RecordingCsv.ExportFileAsync(source, folder.File("drive.csv"));

        Assert.Equal(3, result.Rows);
    }

    // ---- The atomic write --------------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_write_keeps_the_old_destination_and_removes_the_temporary_file()
    {
        using var folder = new TempFolder();
        var target = folder.File("out.csv");
        await File.WriteAllTextAsync(target, "old");

        await Assert.ThrowsAsync<InvalidOperationException>(() => ExportFile.WriteAsync(target, async stream =>
        {
            await stream.WriteAsync("partial"u8.ToArray());
            throw new InvalidOperationException("disk full");
        }));

        Assert.Equal("old", await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(target + ".tmp"));
    }

    [Fact]
    public async Task A_successful_write_replaces_the_destination()
    {
        using var folder = new TempFolder();
        var target = folder.File("out.csv");
        await File.WriteAllTextAsync(target, "old");

        await ExportFile.WriteAsync(target, stream => stream.WriteAsync("new"u8.ToArray()).AsTask());

        Assert.Equal("new", await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(target + ".tmp"));
    }

    // ---- Laps page ---------------------------------------------------------------------------------------

    private static LapTracker TrackerWithLaps(params double[] lapSeconds)
    {
        var tracker = new LapTracker();
        double distance = 0;
        for (var lap = 0; lap <= lapSeconds.Length; lap++)
        {
            var seconds = lap < lapSeconds.Length ? lapSeconds[lap] : 1;
            for (var t = 0.0; t <= seconds + 1e-9; t += 0.5)
            {
                tracker.OnPacket(TestPackets.Sample() with
                {
                    LapNumber = (ushort)lap,
                    CurrentLap = (float)t,
                    DistanceTraveled = (float)(distance + (t * 50)),
                    LastLap = lap == 0 ? 0 : (float)lapSeconds[lap - 1],
                });
            }

            distance += seconds * 50;
        }

        return tracker;
    }

    private static LapsViewModel LapsPage(LapTracker tracker, IFileSaveDialog? dialog, TimeProvider? time = null)
    {
        var vm = new LapsViewModel(tracker, dialog, time: time);
        vm.Refresh(TestPackets.Sample());
        return vm;
    }

    [Fact]
    public void The_laps_export_is_disabled_without_laps_or_without_a_dialog_and_says_why()
    {
        var empty = LapsPage(new LapTracker(), new FakeFileSaveDialog("x.csv"));
        var noDialog = LapsPage(TrackerWithLaps(20), dialog: null);
        var ready = LapsPage(TrackerWithLaps(20), new FakeFileSaveDialog("x.csv"));

        Assert.False(empty.ExportCsvCommand.CanExecute(null));
        Assert.Equal(ExportStrings.ExportLapsDisabledTooltip, empty.ExportTooltip);
        Assert.False(noDialog.ExportCsvCommand.CanExecute(null));
        Assert.True(ready.ExportCsvCommand.CanExecute(null));
        Assert.Equal(ExportStrings.ExportLapsTooltip, ready.ExportTooltip);
    }

    [Fact]
    public async Task Exporting_the_laps_asks_where_and_writes_the_file()
    {
        using var folder = new TempFolder();
        var dialog = new FakeFileSaveDialog(folder.File("laps.csv"));
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 9, 5, 0, TimeSpan.Zero));
        var vm = LapsPage(TrackerWithLaps(20, 22), dialog, clock);

        await vm.ExportCsvCommand.ExecuteAsync(null);

        var asked = Assert.Single(dialog.Asked);
        Assert.Equal(ExportStrings.ExportDialogTitle, asked.Title);
        Assert.Matches(@"^.+-2026-10-18_\d\d-05\.csv$", asked.SuggestedName);
        Assert.Contains("*.csv", asked.Filter);
        Assert.Equal(["lap,time_s,gap_to_best_s", "1,20.000,0.000", "2,22.000,2.000"], Lines(Decode(await File.ReadAllBytesAsync(folder.File("laps.csv")))));
        Assert.False(vm.ExportStatus.IsError);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, ExportStrings.ExportSaved, "laps.csv"), vm.ExportStatus.Text);
        Assert.Equal(folder.File("laps.csv"), vm.ExportStatus.Tooltip);
    }

    [Fact]
    public async Task Cancelling_the_dialog_writes_nothing_and_says_nothing()
    {
        using var folder = new TempFolder();
        var vm = LapsPage(TrackerWithLaps(20), new FakeFileSaveDialog(null));

        await vm.ExportCsvCommand.ExecuteAsync(null);

        Assert.Empty(Directory.GetFiles(folder.Path));
        Assert.False(vm.ExportStatus.HasText);
    }

    [Fact]
    public async Task A_disk_problem_becomes_the_red_message_not_an_exception()
    {
        using var folder = new TempFolder();
        var vm = LapsPage(TrackerWithLaps(20), new FakeFileSaveDialog(folder.File(Path.Combine("missing", "laps.csv"))));

        await vm.ExportCsvCommand.ExecuteAsync(null); // must not throw

        Assert.True(vm.ExportStatus.IsError);
        Assert.Contains("laps.csv", vm.ExportStatus.Text);
        Assert.False(File.Exists(Path.Combine(folder.Path, "missing", "laps.csv.tmp")));
    }

    [Fact]
    public async Task A_new_session_clears_the_message_about_the_old_file()
    {
        using var folder = new TempFolder();
        var vm = LapsPage(TrackerWithLaps(20), new FakeFileSaveDialog(folder.File("laps.csv")));
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.True(vm.ExportStatus.HasText);

        vm.BeginSession();

        Assert.False(vm.ExportStatus.HasText);
    }

    // ---- Performance page --------------------------------------------------------------------------------

    [Fact]
    public async Task The_performance_export_covers_the_stored_history_and_not_the_table_unit()
    {
        using var folder = new TempFolder();
        var names = new CarNameStore(folder.File("cars.json"));
        names.TrySet(2211, "Daily");
        var history = new PerformanceHistoryStore(folder.File("performance.json"));
        var topSpeed = new TopSpeedTracker();
        var acceleration = new AccelerationTimer();
        var braking = new BrakingTest();
        var dialog = new FakeFileSaveDialog(folder.File("perf.csv"));
        var units = new UnitsService { SpeedUnit = SpeedUnit.Mph }; // the table is in mph...
        var vm = new PerformanceViewModel(
            acceleration, topSpeed, new CarChangeDetector(topSpeed, acceleration, braking), history, units, carNames: new CarNames(store: names),
            braking: braking, saveDialog: dialog);
        history.Merge(new CarKey(2211, 780), 5, 2, new PerformanceResult(3.5, null, null, null, 300, ZeroTo60MphSeconds: 3.3));
        Assert.True(vm.ExportCsvCommand.CanExecute(null));

        await vm.ExportCsvCommand.ExecuteAsync(null);

        var lines = Lines(Decode(await File.ReadAllBytesAsync(folder.File("perf.csv"))));
        Assert.Equal(PerformanceCsv.Columns.Length, lines[0].Split(',').Length);
        Assert.StartsWith("2211,Daily,780,S2,AWD,3.500,,,3.300,", lines[1]); // ...and the file carries km/h and mph values both
        Assert.Contains(",300.0,", lines[1]);
        Assert.Matches(
            "^" + ExportStrings.ExportPerformanceFileName + @"-\d{4}-\d\d-\d\d_\d\d-\d\d\.csv$", Assert.Single(dialog.Asked).SuggestedName);
        Assert.False(vm.ExportStatus.IsError);
    }

    [Fact]
    public async Task The_performance_export_car_name_is_the_nickname_then_the_catalog_name_and_empty_when_unknown()
    {
        using var folder = new TempFolder();
        var catalog = CarCatalog.Load(new MemoryStream("""{ "cars": { "3983": "2024 BMW X6 M", "500": "1999 Test Car" } }"""u8.ToArray()));
        var store = new CarNameStore(folder.File("cars.json"));
        store.TrySet(500, "Mine");
        var names = new CarNames(catalog, store);
        var history = new PerformanceHistoryStore(folder.File("performance.json"));
        var topSpeed = new TopSpeedTracker();
        var acceleration = new AccelerationTimer();
        var braking = new BrakingTest();
        var dialog = new FakeFileSaveDialog(folder.File("perf.csv"));
        var vm = new PerformanceViewModel(
            acceleration, topSpeed, new CarChangeDetector(topSpeed, acceleration, braking), history, carNames: names,
            braking: braking, saveDialog: dialog);
        foreach (var ordinal in new[] { 3983, 500, 77 })
        {
            history.Merge(new CarKey(ordinal, 700), 5, 2, new PerformanceResult(3.5, null, null, null, 300));
        }

        await vm.ExportCsvCommand.ExecuteAsync(null);

        var lines = Lines(Decode(await File.ReadAllBytesAsync(folder.File("perf.csv"))));
        Assert.StartsWith("77,,700,", lines[1]); // unknown: empty, never the translated "Car #77" placeholder
        Assert.StartsWith("500,Mine,700,", lines[2]);
        Assert.StartsWith("3983,2024 BMW X6 M,700,", lines[3]);
    }

    [Fact]
    public void The_performance_export_is_disabled_until_there_is_a_history()
    {
        using var folder = new TempFolder();
        var history = new PerformanceHistoryStore(folder.File("performance.json"));
        var topSpeed = new TopSpeedTracker();
        var acceleration = new AccelerationTimer();
        var braking = new BrakingTest();
        var vm = new PerformanceViewModel(
            acceleration, topSpeed, new CarChangeDetector(topSpeed, acceleration, braking), history,
            saveDialog: new FakeFileSaveDialog(folder.File("p.csv")));

        Assert.False(vm.ExportCsvCommand.CanExecute(null));
        Assert.Equal(ExportStrings.ExportPerformanceDisabledTooltip, vm.ExportTooltip);

        history.Merge(new CarKey(1, 500), 1, 0, new PerformanceResult(5.0, null, null, null, null));

        Assert.True(vm.ExportCsvCommand.CanExecute(null));
        Assert.Equal(ExportStrings.ExportPerformanceTooltip, vm.ExportTooltip);
    }

    // ---- Sessions page -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_recording_is_exported_from_its_row_with_a_saved_message()
    {
        using var folder = new TempFolder();
        var dialog = new FakeFileSaveDialog(folder.File("out.csv"));
        using var rig = new SessionsRig(saveDialog: dialog);
        await rig.AddRecordingAsync("drive", packets: 12);
        var row = Assert.Single(await rig.LoadLibraryAsync());
        Assert.True(row.ExportCommand.CanExecute(null));

        await row.ExportCommand.ExecuteAsync(null);

        Assert.Equal("drive.csv", Assert.Single(dialog.Asked).SuggestedName);
        Assert.Equal(13, Lines(Decode(await File.ReadAllBytesAsync(folder.File("out.csv")))).Length);
        Assert.False(rig.Sessions.NoticeIsError);
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, ExportStrings.ExportSavedPackets, "out.csv", 12),
            rig.Sessions.Notice);
        Assert.False(rig.Sessions.IsExporting);
        Assert.True(row.ExportCommand.CanExecute(null)); // usable again
    }

    [Fact]
    public async Task Cancelling_the_save_dialog_exports_nothing()
    {
        using var rig = new SessionsRig(saveDialog: new FakeFileSaveDialog(null));
        await rig.AddRecordingAsync("drive");
        var row = Assert.Single(await rig.LoadLibraryAsync());

        await row.ExportCommand.ExecuteAsync(null);

        Assert.False(rig.Sessions.IsExporting);
        Assert.False(rig.Sessions.HasNotice);
    }

    [Fact]
    public async Task A_damaged_recording_gives_an_error_message_and_frees_the_buttons()
    {
        using var folder = new TempFolder();
        using var rig = new SessionsRig(saveDialog: new FakeFileSaveDialog(folder.File("out.csv")));
        var path = await rig.AddRecordingAsync("drive");
        var row = Assert.Single(await rig.LoadLibraryAsync());
        await File.WriteAllTextAsync(path, "not a recording any more"); // damaged after it was listed

        await row.ExportCommand.ExecuteAsync(null);

        Assert.True(rig.Sessions.NoticeIsError);
        Assert.Contains("out.csv", rig.Sessions.Notice);
        Assert.False(rig.Sessions.IsExporting);
        Assert.False(File.Exists(folder.File("out.csv")));
    }

    [Fact]
    public async Task Export_is_unavailable_without_a_dialog_and_for_an_unreadable_recording()
    {
        using var rig = new SessionsRig();
        await rig.AddRecordingAsync("drive");
        var row = Assert.Single(await rig.LoadLibraryAsync());
        Assert.False(row.ExportCommand.CanExecute(null)); // no dialog to ask with

        using var withDialog = new SessionsRig(saveDialog: new FakeFileSaveDialog("x.csv"));
        Directory.CreateDirectory(withDialog.Folder);
        await File.WriteAllTextAsync(Path.Combine(withDialog.Folder, "bad.fh6rec"), "junk");
        var rows = await withDialog.LoadLibraryAsync();
        var bad = Assert.Single(rows);
        Assert.True(bad.IsUnreadable);
        Assert.False(bad.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task After_shutdown_an_export_does_not_start()
    {
        using var folder = new TempFolder();
        var dialog = new FakeFileSaveDialog(folder.File("out.csv"));
        using var rig = new SessionsRig(saveDialog: dialog);
        await rig.AddRecordingAsync("drive");
        var row = Assert.Single(await rig.LoadLibraryAsync());

        rig.Sessions.Shutdown();
        await row.ExportCommand.ExecuteAsync(null);

        Assert.Empty(dialog.Asked);
    }

    [Fact]
    public void Cancelling_when_nothing_is_exporting_does_nothing()
    {
        using var rig = new SessionsRig(saveDialog: new FakeFileSaveDialog(null));

        rig.Sessions.CancelExportCommand.Execute(null);

        Assert.False(rig.Sessions.IsExporting);
    }

    // ---- The runner: no exception escapes, whatever the writer throws ------------------------------------

    [Fact]
    public async Task A_writer_that_throws_anything_becomes_the_red_message_not_an_exception()
    {
        using var folder = new TempFolder();
        var runner = new CsvExportRunner(new FakeFileSaveDialog(folder.File("out.csv")), new ExportFeedback());

        var written = await runner.RunAsync("out.csv", _ => throw new InvalidOperationException("bug in the writer")); // not an IOException

        Assert.False(written);
        Assert.True(runner.Feedback.IsError);
        Assert.Contains("bug in the writer", runner.Feedback.Text);
        Assert.False(File.Exists(folder.File("out.csv")));
        Assert.False(File.Exists(folder.File("out.csv.tmp")));
    }

    [Fact]
    public async Task A_cancelled_writer_says_cancelled_and_is_not_an_error()
    {
        using var folder = new TempFolder();
        var runner = new CsvExportRunner(new FakeFileSaveDialog(folder.File("out.csv")), new ExportFeedback());

        var written = await runner.RunAsync("out.csv", _ => throw new OperationCanceledException());

        Assert.False(written);
        Assert.False(runner.Feedback.IsError);
        Assert.Equal(ExportStrings.ExportCancelled, runner.Feedback.Text);
        Assert.False(File.Exists(folder.File("out.csv")));
    }

    // ---- Sessions page: truncated recordings and rows being exported ------------------------------------

    [Fact]
    public async Task Exporting_a_recording_cut_off_by_a_crash_says_so()
    {
        using var folder = new TempFolder();
        using var rig = new SessionsRig(saveDialog: new FakeFileSaveDialog(folder.File("out.csv")));
        var path = await rig.AddRecordingAsync("drive", packets: 12);
        var row = Assert.Single(await rig.LoadLibraryAsync());
        var bytes = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, bytes[..^5]); // the app died in the middle of the last record

        await row.ExportCommand.ExecuteAsync(null);

        Assert.False(rig.Sessions.NoticeIsError);
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, ExportStrings.ExportSavedPacketsTruncated, "out.csv", 11),
            rig.Sessions.Notice);
    }

    [Fact]
    public async Task A_row_being_exported_cannot_be_renamed_or_deleted_until_the_export_ends()
    {
        using var folder = new TempFolder();
        using var rig = new SessionsRig(saveDialog: new FakeFileSaveDialog(folder.File("out.csv")));
        await rig.AddRecordingAsync("drive");
        var row = Assert.Single(await rig.LoadLibraryAsync());
        Assert.True(row.CanModify);

        // The export runs on another thread, so look at the row at the moment it is flagged, on this (the UI) thread.
        bool? modifiableWhileExporting = null;
        bool? editingWhileExporting = null;
        string? renameTooltip = null;
        rig.Sessions.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionsViewModel.IsExporting) && rig.Sessions.IsExporting)
            {
                modifiableWhileExporting = row.CanModify;
                row.BeginRenameCommand.Execute(null);
                editingWhileExporting = row.IsEditing;
                renameTooltip = row.RenameTooltip;
            }
        };

        await row.ExportCommand.ExecuteAsync(null);

        Assert.False(modifiableWhileExporting);
        Assert.False(editingWhileExporting);
        Assert.Equal(SessionsStrings.SessionsExportingTooltip, renameTooltip);
        Assert.True(row.CanModify); // free again
        Assert.Equal(SessionsStrings.SessionsRename, row.RenameTooltip);
    }

    [Fact]
    public async Task A_row_rebuilt_while_its_file_is_being_exported_is_still_locked()
    {
        using var folder = new TempFolder();
        using var rig = new SessionsRig(saveDialog: new FakeFileSaveDialog(folder.File("out.csv")));
        await rig.AddRecordingAsync("drive");
        await rig.LoadLibraryAsync();

        bool? rebuiltRowModifiable = null;
        rig.Sessions.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionsViewModel.IsExporting) && rig.Sessions.IsExporting)
            {
                // What a library re-read does: new row objects for the same files.
                var rebuilt = new RecordingRowViewModel(rig.Sessions.Rows[0].Item, rig.Sessions, isPlaying: false);
                rebuiltRowModifiable = rebuilt.CanModify;
            }
        };

        await rig.Sessions.Rows[0].ExportCommand.ExecuteAsync(null);

        Assert.False(rebuiltRowModifiable);
    }
}
