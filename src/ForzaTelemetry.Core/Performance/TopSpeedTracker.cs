namespace ForzaTelemetry.Core.Performance;

/// <summary>
/// Tracks the highest speed seen while the game is actively simulating.
/// </summary>
/// <remarks>
/// Thread-safety: single writer (<see cref="OnPacket"/> on the receive loop) and any number of readers.
/// The value is one 64-bit double; <see cref="Volatile"/> guarantees readers never see a torn value nor
/// a stale cached one. <see cref="Reset"/> may be called from the UI thread: if it races with a new
/// maximum the worst case is losing that one sample, which is harmless for a "reset" button.
/// </remarks>
public sealed class TopSpeedTracker : IPacketObserver
{
    private double _topSpeedMs;

    /// <summary>Highest speed in meters per second since the last reset (0 when none).</summary>
    public double TopSpeedMs => Volatile.Read(ref _topSpeedMs);

    /// <summary>Highest speed in kilometers per hour since the last reset.</summary>
    public double TopSpeedKmh => TopSpeedMs * 3.6;

    /// <inheritdoc />
    public void OnPacket(TelemetryPacket packet)
    {
        // Menus/pause send IsRaceOn = false with stale values; a non-finite speed would poison the max forever.
        if (!packet.IsRaceOn || !float.IsFinite(packet.Speed))
        {
            return;
        }

        if (packet.Speed > _topSpeedMs)
        {
            Volatile.Write(ref _topSpeedMs, packet.Speed);
        }
    }

    /// <summary>Forgets the recorded maximum.</summary>
    public void Reset() => Volatile.Write(ref _topSpeedMs, 0d);
}
