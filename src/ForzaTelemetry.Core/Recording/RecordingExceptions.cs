namespace ForzaTelemetry.Core.Recording;

/// <summary>
/// The stream is not a readable <c>.fh6rec</c> recording (wrong magic, cut-off header, impossible header
/// field). Derives from <see cref="IOException"/> (<see cref="InvalidDataException"/> is sealed), so generic
/// "the file could not be read" handlers still catch it.
/// </summary>
public class InvalidRecordingException(string message) : IOException(message);

/// <summary>
/// The recording was written by a newer version of the app. Distinct from <see cref="InvalidRecordingException"/>
/// so the UI can say "update the app" instead of "file is corrupt".
/// </summary>
public sealed class UnsupportedRecordingVersionException(ushort fileVersion, ushort supportedVersion)
    : InvalidRecordingException(
        $"The recording uses format version {fileVersion}, but this app only understands up to version {supportedVersion}.")
{
    /// <summary>Version stored in the file.</summary>
    public ushort FileVersion { get; } = fileVersion;

    /// <summary>Highest version this build can read.</summary>
    public ushort SupportedVersion { get; } = supportedVersion;
}
