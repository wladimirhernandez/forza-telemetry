using System.Runtime.CompilerServices;

namespace ForzaTelemetry.Core.Recording;

/// <summary>
/// An <see cref="ITelemetrySource"/> that replays a recording instead of listening on a socket: the
/// second ADAPTER for the telemetry port, so <c>TelemetryService</c> and the view model cannot tell
/// replay from live play.
/// </summary>
/// <remarks>
/// Raw datagrams are re-parsed with <see cref="TelemetryParser"/> on every replay, which is the whole point
/// of recording raw bytes: a parser fix improves old recordings. Datagrams that do not parse are skipped
/// and counted, exactly like the live listener does. Playback controls (<see cref="Speed"/>,
/// <see cref="Pause"/>, <see cref="Resume"/>, <see cref="SeekToStart"/>, <see cref="Position"/>,
/// <see cref="Duration"/>) delegate to the underlying <see cref="ReplayDatagramSource"/>; see it for the timing model.
/// </remarks>
public sealed class ReplayTelemetrySource : ITelemetrySource
{
    private readonly ReplayDatagramSource _datagrams;
    private long _discardedCount;

    /// <summary>Wraps an existing datagram replay.</summary>
    public ReplayTelemetrySource(ReplayDatagramSource datagrams) =>
        _datagrams = datagrams ?? throw new ArgumentNullException(nameof(datagrams));

    /// <summary>Replays a recording opened through <paramref name="openRecording"/> (see <see cref="ReplayDatagramSource"/>).</summary>
    public ReplayTelemetrySource(Func<Stream> openRecording, TimeProvider? timeProvider = null)
        : this(new ReplayDatagramSource(openRecording, timeProvider))
    {
    }

    /// <summary>Replays the recording file at <paramref name="path"/>.</summary>
    public ReplayTelemetrySource(string path, TimeProvider? timeProvider = null)
        : this(new ReplayDatagramSource(path, timeProvider))
    {
    }

    /// <summary>Playback speed multiplier; see <see cref="ReplaySpeed"/>.</summary>
    public double Speed
    {
        get => _datagrams.Speed;
        set => _datagrams.Speed = value;
    }

    /// <summary>Whether playback is held by <see cref="Pause"/>.</summary>
    public bool IsPaused => _datagrams.IsPaused;

    /// <summary>Recording time reached so far (progress numerator).</summary>
    public TimeSpan Position => _datagrams.Position;

    /// <summary>Total recording length (progress denominator); zero until playback has started.</summary>
    public TimeSpan Duration => _datagrams.Duration;

    /// <summary>Datagrams skipped because they were not a valid 324-byte packet.</summary>
    public long DiscardedCount => Interlocked.Read(ref _discardedCount);

    /// <summary>Holds playback; see <see cref="ReplayDatagramSource.Pause"/>.</summary>
    public void Pause() => _datagrams.Pause();

    /// <summary>Continues playback.</summary>
    public void Resume() => _datagrams.Resume();

    /// <summary>Restarts from the first record.</summary>
    public void SeekToStart() => _datagrams.SeekToStart();

    /// <summary>
    /// Yields the parsed packets of the recording with their original spacing. The enumeration ends when
    /// the recording ends or <paramref name="ct"/> is cancelled.
    /// </summary>
    public async IAsyncEnumerable<TelemetryPacket> ReadPacketsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var datagram in _datagrams.ReadDatagramsAsync(ct).ConfigureAwait(false))
        {
            if (TelemetryParser.TryParse(datagram.Span, out var packet))
            {
                yield return packet;
            }
            else
            {
                Interlocked.Increment(ref _discardedCount);
            }
        }
    }

    /// <summary>Nothing to release: every stream is opened and closed inside the enumeration.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
