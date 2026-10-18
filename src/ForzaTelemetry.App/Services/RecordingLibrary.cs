using System.IO;
using ForzaTelemetry.Core.Recording;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForzaTelemetry.App.Services;

/// <summary>One recording on disk, with what a "Sessions" list needs to show about it.</summary>
/// <param name="Path">Full path of the <c>.fh6rec</c> file.</param>
/// <param name="Name">The file name without folder and extension: what the user sees, and what Rename edits.</param>
/// <param name="StartedAt">When the recording began (from its header, or the file time for an unreadable file).</param>
/// <param name="Duration">Length of the recording.</param>
/// <param name="SizeBytes">File size.</param>
/// <param name="PacketCount">Datagrams stored.</param>
/// <param name="CarOrdinals">Cars seen while the race was on (see <see cref="RecordingFileInfo.CarOrdinals"/>).</param>
/// <param name="IsTruncated">The file ended in a half-written record (the app crashed while recording).</param>
/// <param name="IsReadable">False when the file is not a recording this build can read (damaged, or from a newer version).</param>
public sealed record RecordingItem(
    string Path,
    string Name,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    long SizeBytes,
    long PacketCount,
    IReadOnlyList<int> CarOrdinals,
    bool IsTruncated,
    bool IsReadable);

/// <summary>Why a proposed recording name was refused.</summary>
public enum RecordingNameError
{
    /// <summary>The name is acceptable.</summary>
    None,

    /// <summary>Empty, or only white space.</summary>
    Empty,

    /// <summary>Contains a character Windows does not allow in a file name (<c>\ / : * ? " &lt; &gt; |</c>, control characters).</summary>
    InvalidCharacters,

    /// <summary>A device name Windows reserves (<c>CON</c>, <c>NUL</c>, <c>COM1</c>, ...): opening it would not open a file.</summary>
    ReservedName,

    /// <summary>Ends with a dot or a space, which Windows silently strips and so would not be the name the user typed.</summary>
    TrailingDotOrSpace,

    /// <summary>Longer than <see cref="RecordingNameValidator.MaxLength"/>.</summary>
    TooLong
}

/// <summary>Checks a name typed for a recording BEFORE the file system is asked, so the dialog can say what is wrong.</summary>
public static class RecordingNameValidator
{
    /// <summary>Longest name accepted (without extension). Far below the 260-character path limit even in a deep folder.</summary>
    public const int MaxLength = 100;

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    /// <summary>
    /// Validates <paramref name="name"/>, which is the name WITHOUT the extension (callers strip a typed <c>.fh6rec</c> first,
    /// see <see cref="Normalize"/>).
    /// </summary>
    public static RecordingNameError Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return RecordingNameError.Empty;
        }

        if (name.Length > MaxLength)
        {
            return RecordingNameError.TooLong;
        }

        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            return RecordingNameError.InvalidCharacters;
        }

        if (name[^1] is '.' or ' ' || name[0] == ' ')
        {
            return RecordingNameError.TrailingDotOrSpace;
        }

        // Windows treats "CON", "CON.txt" and "con.anything" alike: the part before the first dot is what counts.
        var stem = name.Split('.')[0].TrimEnd();
        if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            return RecordingNameError.ReservedName;
        }

        return RecordingNameError.None;
    }

    /// <summary>
    /// What the user typed, reduced to the bare name: trimmed, and without a trailing <c>.fh6rec</c> (people type the extension
    /// out of habit, and "name.fh6rec.fh6rec" would be a bug nobody asked for).
    /// </summary>
    public static string Normalize(string? typed)
    {
        var name = (typed ?? string.Empty).Trim();
        return name.EndsWith(RecordingLibrary.Extension, StringComparison.OrdinalIgnoreCase)
            ? name[..^RecordingLibrary.Extension.Length].TrimEnd()
            : name;
    }
}

/// <summary>The outcome of <see cref="RecordingLibrary.TryRename"/>.</summary>
public enum RecordingRenameResult
{
    /// <summary>Renamed.</summary>
    Renamed,

    /// <summary>The name is not acceptable (see <see cref="RecordingNameValidator"/>).</summary>
    InvalidName,

    /// <summary>Another recording already has that name.</summary>
    AlreadyExists,

    /// <summary>The file system refused (the file is in use, read-only, gone).</summary>
    Failed
}

/// <summary>
/// The folder of recordings: where new ones go, what is in it, and the file operations on them (rename, delete).
/// </summary>
/// <remarks>
/// <para>
/// <b>Where.</b> <c>%APPDATA%\ForzaTelemetry\recordings</c>, next to the settings, created on demand: a user who never records
/// gets no empty folder, and Open folder still works (it creates it first).
/// </para>
/// <para>
/// <b>Never overwrite.</b> A recording cannot be repeated (it is a drive), so a name that is taken gets a numeric suffix instead
/// of replacing the file, and the recorder itself opens with <c>CreateNew</c> as a second line of defence.
/// </para>
/// <para>
/// <b>Scanning off the UI thread, and cached.</b> Listing needs a header read plus a pass over every record of every file
/// (<see cref="RecordingFileInfo.ScanFileAsync"/>), which is cheap for one file and noticeable for fifty. It runs on the thread
/// pool, and a file whose path, size and modification time did not change is not scanned again (the Sessions tab rebuilds its
/// view, and so asks again, every time it is opened).
/// </para>
/// </remarks>
public sealed class RecordingLibrary
{
    /// <summary>The file extension of a recording, with the dot.</summary>
    public const string Extension = ".fh6rec";

    private readonly string _folder;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Dictionary<string, (long Length, DateTime WrittenUtc, RecordingItem Item)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _cacheGate = new();

    /// <param name="folder">The recordings folder (not created until needed).</param>
    /// <param name="timeProvider">Clock for file names (local time); defaults to the system clock.</param>
    /// <param name="logger">Receives failures; optional.</param>
    public RecordingLibrary(string folder, TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        _folder = folder;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary><c>%APPDATA%\ForzaTelemetry\recordings</c>.</summary>
    public static string DefaultFolder { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ForzaTelemetry", "recordings");

    /// <summary>The recordings folder.</summary>
    public string Folder => _folder;

    /// <summary>
    /// A path for a new recording, named after the LOCAL time it starts at (<c>2026-10-18_01-23-45.fh6rec</c>), in a folder that
    /// now exists. If that name is taken (two recordings in the same second) a <c>-2</c>, <c>-3</c>... suffix is added.
    /// </summary>
    public string NextRecordingPath()
    {
        Directory.CreateDirectory(_folder);

        var stamp = _time.GetLocalNow().ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
        var path = System.IO.Path.Combine(_folder, stamp + Extension);
        for (var n = 2; File.Exists(path); n++)
        {
            path = System.IO.Path.Combine(_folder, $"{stamp}-{n}{Extension}");
        }

        return path;
    }

    /// <summary>
    /// Lists the recordings, newest first. Runs on the thread pool. An unreadable file is listed (flagged) rather than hidden, so
    /// the user can still delete it. A missing folder is an empty library.
    /// </summary>
    /// <param name="excludePath">A file to leave out: the one being recorded right now (incomplete by definition).</param>
    public Task<IReadOnlyList<RecordingItem>> ScanAsync(string? excludePath = null, CancellationToken ct = default) =>
        Task.Run(() => ScanCoreAsync(excludePath, ct), ct);

    private async Task<IReadOnlyList<RecordingItem>> ScanCoreAsync(string? excludePath, CancellationToken ct)
    {
        if (!Directory.Exists(_folder))
        {
            return [];
        }

        var items = new List<RecordingItem>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_folder, "*" + Extension).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.LibraryUnreadable(_logger, ex, _folder);
            return [];
        }

        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();

            if (excludePath is not null && string.Equals(path, excludePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var item = await ReadItemAsync(path, ct).ConfigureAwait(false);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items.OrderByDescending(i => i.StartedAt).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<RecordingItem?> ReadItemAsync(string path, CancellationToken ct)
    {
        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists)
            {
                return null; // deleted between the listing and now
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        lock (_cacheGate)
        {
            if (_cache.TryGetValue(path, out var cached) && cached.Length == file.Length && cached.WrittenUtc == file.LastWriteTimeUtc)
            {
                return cached.Item;
            }
        }

        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        RecordingItem item;
        try
        {
            var info = await RecordingFileInfo.ScanFileAsync(path, ct).ConfigureAwait(false);
            item = new RecordingItem(
                path, name, info.StartedAtUtc.ToLocalTime(), info.Duration, file.Length, info.PacketCount, info.CarOrdinals,
                info.IsTruncated, IsReadable: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Not a recording (or one from a newer build, or damaged): still a file the user may want to get rid of.
            AppLog.RecordingUnreadable(_logger, ex, path);
            item = new RecordingItem(
                path, name, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero).ToLocalTime(), TimeSpan.Zero, file.Length, 0, [],
                IsTruncated: false, IsReadable: false);
        }

        lock (_cacheGate)
        {
            _cache[path] = (file.Length, file.LastWriteTimeUtc, item);
        }

        return item;
    }

    /// <summary>
    /// Renames a recording (the extension is kept). Never overwrites: a taken name is refused, so one recording can never
    /// silently destroy another.
    /// </summary>
    /// <param name="path">The recording to rename.</param>
    /// <param name="newName">The name the user typed (see <see cref="RecordingNameValidator.Normalize"/>).</param>
    /// <param name="newPath">The new full path when the result is <see cref="RecordingRenameResult.Renamed"/>.</param>
    public RecordingRenameResult TryRename(string path, string newName, out string newPath)
    {
        newPath = path;
        var name = RecordingNameValidator.Normalize(newName);
        if (RecordingNameValidator.Validate(name) != RecordingNameError.None)
        {
            return RecordingRenameResult.InvalidName;
        }

        var directory = System.IO.Path.GetDirectoryName(path)!;
        var target = System.IO.Path.Combine(directory, name + Extension);

        // Renaming to the same name (or only changing the case) is a no-op, not a "name taken" error.
        if (string.Equals(target, path, StringComparison.Ordinal))
        {
            return RecordingRenameResult.Renamed;
        }

        var onlyCaseDiffers = string.Equals(target, path, StringComparison.OrdinalIgnoreCase);
        if (!onlyCaseDiffers && File.Exists(target))
        {
            return RecordingRenameResult.AlreadyExists;
        }

        try
        {
            // File.Move without overwrite throws if the target appeared since the check above: the race stays safe.
            File.Move(path, target);
        }
        catch (IOException ex) when (File.Exists(target) && !onlyCaseDiffers)
        {
            AppLog.RecordingRenameFailed(_logger, ex, path);
            return RecordingRenameResult.AlreadyExists;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.RecordingRenameFailed(_logger, ex, path);
            return RecordingRenameResult.Failed;
        }

        lock (_cacheGate)
        {
            _cache.Remove(path);
        }

        AppLog.RecordingRenamed(_logger, path, target);
        newPath = target;
        return RecordingRenameResult.Renamed;
    }

    /// <summary>Deletes a recording for good (there is no recycle bin for a file the app owns). False when the file system refused.</summary>
    public bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.RecordingDeleteFailed(_logger, ex, path);
            return false;
        }

        lock (_cacheGate)
        {
            _cache.Remove(path);
        }

        AppLog.RecordingDeleted(_logger, path);
        return true;
    }
}
