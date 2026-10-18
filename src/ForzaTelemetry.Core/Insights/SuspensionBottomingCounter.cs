namespace ForzaTelemetry.Core.Insights;

/// <summary>How many times each corner hit the bump stop.</summary>
public readonly record struct BottomingCounts(int FrontLeft, int FrontRight, int RearLeft, int RearRight)
{
    /// <summary>Total events across the four corners.</summary>
    public int Total => FrontLeft + FrontRight + RearLeft + RearRight;
}

/// <summary>
/// Counts suspension bottoming events per corner.
/// </summary>
/// <remarks>
/// <para><b>Edge detection with hysteresis.</b> A single hard landing keeps travel above 0.98 for many packets;
/// counting each packet would turn one impact into dozens. We count only the ENTRY into the bottomed state
/// (<see cref="BalanceAnalyzer.IsBottomedOut"/>), and re-arm a corner only after its travel falls below
/// <see cref="RearmTravel"/>. The gap between 0.98 and 0.90 stops sensor noise at the threshold from
/// producing repeated counts.</para>
///
/// <para><b>Thread-safety.</b> Single writer; each counter is an <c>int</c> written with
/// <see cref="Volatile.Write(ref int, int)"/> and read with <see cref="Volatile.Read(ref int)"/>. A reader may
/// observe corner counts from slightly different packets, which is irrelevant for a running tally.
/// <see cref="Reset"/> may be called from any thread: it only raises a flag that the writer applies at the
/// start of its next packet (so the writer's private edge-detection state is never touched from another
/// thread), and <see cref="Counts"/> reports zeros while a reset is pending.</para>
/// </remarks>
public sealed class SuspensionBottomingCounter : IPacketObserver
{
    /// <summary>Travel must fall below this before the next bottoming event of a corner can be counted.</summary>
    public const float RearmTravel = 0.90f;

    private readonly int[] _counts = new int[4];
    private readonly bool[] _bottomed = new bool[4];
    private int _resetRequested;

    /// <summary>Current tallies; safe from any thread.</summary>
    public BottomingCounts Counts => Volatile.Read(ref _resetRequested) == 1
        ? default
        : new(
            Volatile.Read(ref _counts[0]),
            Volatile.Read(ref _counts[1]),
            Volatile.Read(ref _counts[2]),
            Volatile.Read(ref _counts[3]));

    /// <inheritdoc />
    public void OnPacket(TelemetryPacket packet)
    {
        if (Interlocked.Exchange(ref _resetRequested, 0) == 1)
        {
            for (var i = 0; i < _counts.Length; i++)
            {
                Volatile.Write(ref _counts[i], 0);
                _bottomed[i] = false;
            }
        }

        if (!packet.IsRaceOn)
        {
            return;
        }

        Update(0, packet.NormalizedSuspensionTravelFrontLeft);
        Update(1, packet.NormalizedSuspensionTravelFrontRight);
        Update(2, packet.NormalizedSuspensionTravelRearLeft);
        Update(3, packet.NormalizedSuspensionTravelRearRight);
    }

    /// <summary>Zeroes the tallies (applied by the writer on its next packet). Safe from any thread.</summary>
    public void Reset() => Volatile.Write(ref _resetRequested, 1);

    private void Update(int corner, float travel)
    {
        if (!_bottomed[corner])
        {
            if (BalanceAnalyzer.IsBottomedOut(travel))
            {
                _bottomed[corner] = true;
                Volatile.Write(ref _counts[corner], _counts[corner] + 1);
            }
        }
        else if (travel < RearmTravel)
        {
            _bottomed[corner] = false;
        }
    }
}
