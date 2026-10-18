using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ForzaTelemetry.App.Resources;

namespace ForzaTelemetry.App.Services;

/// <summary>
/// The whole "Export CSV" action for a table that is already in memory (laps, performance history): ask where to save, write the
/// file off the UI thread, and say how it went.
/// </summary>
/// <remarks>
/// One class so the two pages cannot drift apart: the same dialog wording, the same atomic write (see <see cref="ExportFile"/>),
/// the same message on success and on failure, and no exception can reach the global error dialog (a disk or permission problem is a
/// normal outcome of exporting, so it becomes the red message next to the button).
/// </remarks>
public sealed class CsvExportRunner
{
    private readonly IFileSaveDialog _dialog;
    private readonly ILogger _logger;

    /// <param name="dialog">Asks where to save.</param>
    /// <param name="feedback">Where the outcome is reported.</param>
    /// <param name="logger">Receives failures; optional.</param>
    public CsvExportRunner(IFileSaveDialog dialog, ExportFeedback feedback, ILogger? logger = null)
    {
        _dialog = dialog;
        Feedback = feedback;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The message line this runner reports to.</summary>
    public ExportFeedback Feedback { get; }

    /// <summary>The Windows dialog filter for CSV files, in the current language.</summary>
    internal static string Filter => $"{ExportStrings.ExportFilterLabel} (*.csv)|*.csv";

    /// <summary>
    /// Runs the action. Must be called on the UI thread (it shows the dialog); the write itself runs on the thread pool.
    /// </summary>
    /// <param name="suggestedFileName">The name offered in the dialog, with <c>.csv</c>.</param>
    /// <param name="write">Writes the table into the stream it is given. It must only touch data it owns (a snapshot), never UI state.</param>
    /// <returns>True when a file was written; false when the user cancelled the dialog or the write failed (the line says which).</returns>
    public async Task<bool> RunAsync(string suggestedFileName, Action<Stream> write)
    {
        var path = _dialog.PickSavePath(ExportStrings.ExportDialogTitle, suggestedFileName, Filter);
        if (path is null)
        {
            return false; // cancelled: not an error, so the previous message (if any) stays
        }

        Feedback.Clear();
        try
        {
            await Task.Run(() => ExportFile.WriteAsync(path, stream =>
            {
                write(stream);
                return Task.CompletedTask;
            })).ConfigureAwait(true); // back on the UI thread: the message is bound to the page

            AppLog.ExportSaved(_logger, path);
            Feedback.Saved(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            AppLog.ExportFailed(_logger, ex, path);
            Feedback.Failed(path, ex.Message);
            return false;
        }
    }

    /// <summary>A name like <c>laps-2026-10-18_12-34.csv</c>: the page's word and the local time, so repeated exports do not collide.</summary>
    internal static string SuggestedName(string stem, DateTimeOffset now) =>
        $"{stem}-{now.ToString("yyyy-MM-dd_HH-mm", System.Globalization.CultureInfo.InvariantCulture)}.csv";
}
