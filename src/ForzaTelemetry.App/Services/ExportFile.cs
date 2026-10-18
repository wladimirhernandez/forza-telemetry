using System.IO;

namespace ForzaTelemetry.App.Services;

/// <summary>
/// Writes an export file so that the destination is either untouched or complete, never half written.
/// </summary>
/// <remarks>
/// The user already confirmed replacing an existing file in the Save dialog, but a long export can still fail halfway (the disk
/// fills up, the user cancels, a USB drive is pulled): writing to <c>name.csv.tmp</c> next to the destination and moving it into
/// place at the very end means the old file survives every one of those, and a failure leaves nothing behind (the temporary file
/// is removed). Same idea as <see cref="AtomicFile"/>, for a stream that is too big to hold in memory first.
/// </remarks>
internal static class ExportFile
{
    /// <summary>
    /// Creates <paramref name="path"/> from whatever <paramref name="write"/> puts in the stream it is given.
    /// Throws what the write throws (including <see cref="OperationCanceledException"/>), or <see cref="IOException"/> /
    /// <see cref="UnauthorizedAccessException"/> when the disk refuses; the destination is only replaced on success.
    /// </summary>
    public static async Task WriteAsync(string path, Func<Stream, Task> write)
    {
        var temp = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temp, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, FileOptions.Asynchronous))
            {
                await write(stream).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
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
            // A leftover .tmp is harmless: the next export to the same path overwrites it.
        }
    }
}
