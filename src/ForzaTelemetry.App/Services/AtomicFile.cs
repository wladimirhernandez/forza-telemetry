using System.IO;

namespace ForzaTelemetry.App.Services;

/// <summary>
/// Writes a text file so that it is always either the complete old version or the complete new one, never half of each.
/// </summary>
/// <remarks>
/// Overwriting a file in place and being interrupted (crash, power loss) leaves a half-written file, which then reads as
/// corrupt and silently resets whatever it stored. Writing the full text to <c>name.tmp</c> first and then swapping it in
/// (<see cref="File.Replace(string, string, string?)"/>, an atomic operation on NTFS) means a reader, or the next start of
/// the app, only ever sees a finished file. Shared by every JSON file the app keeps (settings, performance history).
/// </remarks>
internal static class AtomicFile
{
    /// <summary>
    /// Creates the folder if needed and writes <paramref name="contents"/> to <paramref name="path"/> atomically.
    /// Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the disk refuses; a leftover temp
    /// file is removed first (best effort), so the caller only has to decide what a failed save means.
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, contents);

            if (File.Exists(path))
            {
                File.Replace(temp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
            // A leftover .tmp is harmless: the next save overwrites it.
        }
    }
}
