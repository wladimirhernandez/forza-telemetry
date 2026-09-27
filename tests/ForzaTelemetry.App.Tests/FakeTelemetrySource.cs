using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ForzaTelemetry.Core;

namespace ForzaTelemetry.App.Tests;

/// <summary>
/// Test double for <see cref="ITelemetrySource"/>: a <see cref="Channel{T}"/>-backed fake that lets
/// a test push packets on demand (<see cref="Publish"/>) instead of needing a real UDP socket and a
/// real copy of Forza Horizon 6 actually sending packets. <see cref="ReadPacketsAsync"/> reads from
/// the channel exactly like <c>UdpTelemetryListener</c> reads from the network — <c>TelemetryService</c>
/// cannot tell the two apart, which is the entire point of depending on the <see cref="ITelemetrySource"/>
/// port instead of the concrete UDP adapter.
/// </summary>
internal sealed class FakeTelemetrySource : ITelemetrySource
{
    private readonly Channel<TelemetryPacket> _channel = Channel.CreateUnbounded<TelemetryPacket>();
    private Exception? _throwOnNextRead;

    /// <summary>Makes a packet available to the next iteration of <see cref="ReadPacketsAsync"/>.</summary>
    public void Publish(TelemetryPacket packet) => _channel.Writer.TryWrite(packet);

    /// <summary>
    /// Makes the loop throw <paramref name="exception"/> instead of yielding the NEXT published
    /// packet — simulating a mid-stream failure (e.g. a socket error while already receiving), as
    /// opposed to a factory-time failure (which a test simulates by throwing straight from the
    /// <c>Func&lt;ITelemetrySource&gt;</c> passed to <c>TelemetryService</c>'s constructor instead).
    /// </summary>
    public void FailNextReadWith(Exception exception) => _throwOnNextRead = exception;

    public async IAsyncEnumerable<TelemetryPacket> ReadPacketsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var packet in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (_throwOnNextRead is { } exception)
            {
                _throwOnNextRead = null;
                throw exception;
            }

            yield return packet;
        }
    }

    public ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
