namespace ForzaTelemetry.Core.Recording;

/// <summary>One record read from a recording: when it arrived (relative to the start) and its raw bytes.</summary>
/// <param name="Elapsed">Time since <see cref="RecordingHeader.StartedAtUtc"/>, at microsecond resolution.</param>
/// <param name="Payload">The raw datagram. A fresh array per record, so consumers may keep it.</param>
public readonly record struct RecordedDatagram(TimeSpan Elapsed, ReadOnlyMemory<byte> Payload);
