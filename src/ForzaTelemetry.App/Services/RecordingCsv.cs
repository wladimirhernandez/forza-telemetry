using System.IO;
using ForzaTelemetry.Core;
using ForzaTelemetry.Core.Export;
using ForzaTelemetry.Core.Recording;

namespace ForzaTelemetry.App.Services;

/// <summary>A recording converted to CSV: how many packets became rows.</summary>
/// <param name="Rows">Data rows written (the header is not counted).</param>
/// <param name="IsTruncated">The recording itself ended in a half-written record (the app crashed while recording); every complete record was exported.</param>
public readonly record struct RecordingCsvResult(long Rows, bool IsTruncated);

/// <summary>
/// Converts a <c>.fh6rec</c> recording to a CSV table, one row per packet, streaming: the file is read record by record and the CSV is
/// written as it goes, so a recording of any length (a long session is hundreds of thousands of packets) costs a few kilobytes
/// of memory, never the whole file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Columns</b> (units in the name where one applies): <c>time_s</c> (since the recording started, from the file, not the game
/// clock), <c>race_on</c> (1 while driving, 0 in a menu or pause, whose other values are stale: filter on it), <c>speed_kmh</c>,
/// <c>rpm</c>, <c>gear</c> (<c>R</c>, <c>N</c> or the number, as on the dashboard), <c>throttle_pct</c> and <c>brake_pct</c> (0 to 100),
/// <c>steer_pct</c> (-100 full left to 100 full right), <c>pos_x</c> and <c>pos_z</c> (world position, m), <c>distance_m</c> (total
/// distance traveled), <c>lap</c> (the lap in progress, 1-based, as the Laps page numbers it) and <c>current_lap_s</c>.
/// </para>
/// <para>
/// <b>Rows are the packets that PARSE</b>: a datagram of the wrong size is skipped, exactly like the live listener and the replay do.
/// </para>
/// <para>
/// <b>Cancelling and progress.</b> The loop checks the token often and reports the fraction of the file read, so the page can show a
/// progress bar and a Cancel button for a big file. Run it off the UI thread.
/// </para>
/// </remarks>
public static class RecordingCsv
{
    /// <summary>The header row.</summary>
    public static readonly string[] Columns =
    [
        "time_s", "race_on", "speed_kmh", "rpm", "gear", "throttle_pct", "brake_pct", "steer_pct",
        "pos_x", "pos_z", "distance_m", "lap", "current_lap_s"
    ];

    // Progress is reported every this many records: often enough for a smooth bar, rare enough to cost nothing.
    private const int ProgressInterval = 1024;

    /// <summary>
    /// Exports the recording at <paramref name="recordingPath"/> to <paramref name="destinationPath"/>. The destination is only
    /// replaced when the whole export succeeded (see <see cref="ExportFile"/>); a cancelled or failed export leaves nothing behind.
    /// </summary>
    /// <exception cref="InvalidRecordingException">The file is not a recording this build can read.</exception>
    /// <exception cref="OperationCanceledException">Cancelled through <paramref name="ct"/>.</exception>
    public static async Task<RecordingCsvResult> ExportFileAsync(
        string recordingPath, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        // FileShare.ReadWrite: the recording may be the one being replayed right now, which holds it open.
        await using var source = new FileStream(
            recordingPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var result = default(RecordingCsvResult);
        await ExportFile.WriteAsync(
            destinationPath,
            async destination => result = await ExportAsync(source, destination, progress, ct).ConfigureAwait(false)).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Streams the recording in <paramref name="source"/> into <paramref name="destination"/> as CSV. Both streams stay open.
    /// <paramref name="progress"/> receives 0..1 while a seekable source is read (and a final 1).
    /// </summary>
    public static async Task<RecordingCsvResult> ExportAsync(
        Stream source, Stream destination, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await using var reader = await SessionRecordingReader.OpenAsync(source, leaveOpen: true, ct).ConfigureAwait(false);
        using var csv = CsvWriter.Create(destination, leaveOpen: true);
        csv.Row(Columns);

        var length = source.CanSeek ? source.Length : 0;
        long rows = 0, records = 0;

        await foreach (var record in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (++records % ProgressInterval == 0)
            {
                ct.ThrowIfCancellationRequested();
                if (length > 0)
                {
                    progress?.Report(Math.Min(1.0, (double)source.Position / length));
                }
            }

            if (!TelemetryParser.TryParse(record.Payload.Span, out var p))
            {
                continue;
            }

            csv.Field(record.Elapsed.TotalSeconds, "0.000")
                .Field(p.IsRaceOn ? 1 : 0)
                .Field(p.SpeedKmh, "0.00")
                .Field(p.CurrentEngineRpm, "0.0")
                .Field(GearFormatter.Format(p.Gear))
                .Field(p.Accel / 255.0 * 100, "0.0")
                .Field(p.Brake / 255.0 * 100, "0.0")
                .Field(Math.Clamp(p.Steer / 127.0, -1, 1) * 100, "0.0")
                .Field(p.PositionX, "0.00")
                .Field(p.PositionZ, "0.00")
                .Field(p.DistanceTraveled, "0.0")
                .Field(p.LapNumber + 1)
                .Field(p.CurrentLap, "0.000")
                .EndRow();
            rows++;
        }

        csv.Flush();
        progress?.Report(1.0);
        return new RecordingCsvResult(rows, reader.IsTruncated);
    }
}
