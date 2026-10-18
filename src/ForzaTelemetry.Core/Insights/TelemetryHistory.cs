namespace ForzaTelemetry.Core.Insights;

/// <summary>
/// A fixed-capacity ring buffer of the most recent <see cref="TelemetrySample"/>s.
/// </summary>
/// <remarks>
/// <para><b>Ring buffer.</b> A preallocated array plus a write index that wraps around: adding a sample
/// overwrites the oldest one once full, so memory is constant no matter how long the session runs and
/// <see cref="Add"/> never allocates.</para>
///
/// <para><b>Thread-safety.</b> One writer (<see cref="OnPacket"/>) and readers on the UI thread. Samples are
/// multi-field structs, so unsynchronized reads could observe a half-written one (a "torn" read); a short
/// <c>lock</c> around Add and CopyTo is the simplest correct answer. It is uncontended almost always (the
/// reader copies a few thousand samples about 20 times a second) and costs tens of nanoseconds, far below the
/// 16 ms packet interval. <see cref="CopyTo"/> writes into a caller-owned buffer, so a reader can reuse one
/// array and allocate nothing per refresh.</para>
/// </remarks>
public sealed class TelemetryHistory : IPacketObserver
{
    /// <summary>Default capacity: about 60 seconds at 60 packets per second.</summary>
    public const int DefaultCapacity = 3600;

    private readonly TelemetrySample[] _buffer;
    private readonly object _gate = new();
    private int _next;
    private int _count;

    /// <summary>Creates a history holding at most <paramref name="capacity"/> samples.</summary>
    public TelemetryHistory(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _buffer = new TelemetrySample[capacity];
    }

    /// <summary>Maximum number of samples retained.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Samples currently stored.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <inheritdoc />
    public void OnPacket(TelemetryPacket packet)
    {
        // Menus and pause send IsRaceOn = false with stale values; charting them would draw flat lies.
        if (packet.IsRaceOn)
        {
            Add(TelemetrySample.FromPacket(packet));
        }
    }

    /// <summary>Appends a sample, overwriting the oldest when full.</summary>
    public void Add(in TelemetrySample sample)
    {
        lock (_gate)
        {
            _buffer[_next] = sample;
            _next = (_next + 1) % _buffer.Length;
            if (_count < _buffer.Length)
            {
                _count++;
            }
        }
    }

    /// <summary>
    /// Copies the newest samples, oldest first, into <paramref name="destination"/>. If the destination is
    /// smaller than <see cref="Count"/> only the newest <c>destination.Length</c> samples are copied.
    /// </summary>
    /// <returns>How many samples were written.</returns>
    public int CopyTo(Span<TelemetrySample> destination)
    {
        lock (_gate)
        {
            var n = Math.Min(_count, destination.Length);
            // The newest sample sits just before _next; walk back n entries to find the first one wanted.
            var start = (_next - n + _buffer.Length) % _buffer.Length;
            var firstChunk = Math.Min(n, _buffer.Length - start);
            _buffer.AsSpan(start, firstChunk).CopyTo(destination);
            _buffer.AsSpan(0, n - firstChunk).CopyTo(destination[firstChunk..]);
            return n;
        }
    }

    /// <summary>Allocating convenience snapshot of every stored sample, oldest first.</summary>
    public TelemetrySample[] ToArray()
    {
        var result = new TelemetrySample[Count];
        var copied = CopyTo(result);
        return copied == result.Length ? result : result[..copied];
    }

    /// <summary>Removes all samples.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _next = 0;
            _count = 0;
        }
    }
}
