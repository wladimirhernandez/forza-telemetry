namespace ForzaTelemetry.Core.Insights;

/// <summary>One point of a captured power/torque curve.</summary>
/// <param name="Rpm">Center of the 100 rpm bin.</param>
/// <param name="PowerWatts">Best power seen in the bin, watts.</param>
/// <param name="TorqueNewtonMeters">Best torque seen in the bin, N*m.</param>
public readonly record struct DynoPoint(float Rpm, float PowerWatts, float TorqueNewtonMeters);

/// <summary>
/// Builds an engine power/torque curve by recording the best values per RPM bin during full-throttle driving.
/// </summary>
/// <remarks>
/// <para><b>Idea.</b> The game reports instantaneous power and torque. If we only keep samples where the
/// engine is genuinely at full load (throttle at least 95%, clutch fully engaged, handbrake off, a forward
/// gear, race running), each RPM bin collects "wide-open-throttle" values, and keeping the MAXIMUM per bin
/// discards transients (shifts, lift-offs) and converges to the true curve as the player sweeps the rev
/// range in several pulls. Bins are 100 rpm wide: fine enough to show the shape, coarse enough that a
/// single pull fills most of them.</para>
///
/// <para>Power and torque maxima are tracked independently per bin; in a single bin they usually come from
/// the same instant, and the difference is negligible at 100 rpm resolution.</para>
///
/// <para><b>Per car AND tune.</b> Curves are keyed by (<see cref="TelemetryPacket.CarOrdinal"/>,
/// <see cref="TelemetryPacket.CarPerformanceIndex"/>). The same car with different upgrades or tuning has a
/// different curve, and keeping the per-bin maximum would blend them into an envelope that matches neither.
/// The performance index changes whenever parts change, so it is a cheap proxy for "this exact build".
/// Switching to another key starts a fresh curve (or resumes it if already seen). <see cref="Reset"/> clears one
/// key.</para>
///
/// <para><b>Thread-safety.</b> Single writer; readers call <see cref="GetCurve"/>, which returns a fresh
/// sorted copy computed under a lock, so nothing mutable escapes.</para>
/// </remarks>
public sealed class DynoCapture : IPacketObserver
{
    /// <summary>Width of an RPM bin.</summary>
    public const int BinWidthRpm = 100;

    /// <summary>Minimum throttle byte (95% of 255, rounded up) for a sample to count.</summary>
    public const byte MinThrottleByte = 242;

    private const int MaxBins = 300; // up to 30,000 rpm; anything above is noise

    private readonly object _gate = new();
    private readonly Dictionary<(int Car, int Pi), (float Power, float Torque)[]> _bins = [];
    private int _currentCar;
    private int _currentPi;

    /// <summary>Performance index of the build currently being driven; 0 before any packet.</summary>
    public int CurrentPerformanceIndex
    {
        get
        {
            lock (_gate)
            {
                return _currentPi;
            }
        }
    }

    /// <summary>CarOrdinal of the most recent accepted or seen packet; 0 before any.</summary>
    public int CurrentCarOrdinal
    {
        get
        {
            lock (_gate)
            {
                return _currentCar;
            }
        }
    }

    /// <inheritdoc />
    public void OnPacket(TelemetryPacket packet)
    {
        lock (_gate)
        {
            if (packet.IsRaceOn)
            {
                _currentCar = packet.CarOrdinal;
                _currentPi = packet.CarPerformanceIndex;
            }

            if (!packet.IsRaceOn
                || packet.Accel < MinThrottleByte
                || packet.Clutch != 0
                || packet.HandBrake != 0
                || packet.Gear < 1
                || !float.IsFinite(packet.CurrentEngineRpm)
                || !float.IsFinite(packet.Power)
                || !float.IsFinite(packet.Torque)
                || packet.CurrentEngineRpm <= 0
                || packet.Power <= 0)
            {
                return;
            }

            var bin = (int)(packet.CurrentEngineRpm / BinWidthRpm);
            if (bin >= MaxBins)
            {
                return;
            }

            var key = (packet.CarOrdinal, packet.CarPerformanceIndex);
            if (!_bins.TryGetValue(key, out var bins))
            {
                bins = new (float, float)[MaxBins];
                _bins[key] = bins;
            }

            ref var slot = ref bins[bin];
            slot = (Math.Max(slot.Power, packet.Power), Math.Max(slot.Torque, packet.Torque));
        }
    }

    /// <summary>The captured curve for a car build as points sorted by RPM; empty when nothing was captured.</summary>
    public IReadOnlyList<DynoPoint> GetCurve(int carOrdinal, int performanceIndex)
    {
        lock (_gate)
        {
            if (!_bins.TryGetValue((carOrdinal, performanceIndex), out var bins))
            {
                return [];
            }

            var points = new List<DynoPoint>();
            for (var i = 0; i < bins.Length; i++)
            {
                if (bins[i].Power > 0)
                {
                    points.Add(new DynoPoint((i * BinWidthRpm) + (BinWidthRpm / 2f), bins[i].Power, bins[i].Torque));
                }
            }

            return points;
        }
    }

    /// <summary>The curve of the car currently being driven.</summary>
    public IReadOnlyList<DynoPoint> GetCurrentCurve() => GetCurve(CurrentCarOrdinal, CurrentPerformanceIndex);

    /// <summary>Forgets one car build's curve.</summary>
    public void Reset(int carOrdinal, int performanceIndex)
    {
        lock (_gate)
        {
            _bins.Remove((carOrdinal, performanceIndex));
        }
    }

    /// <summary>Forgets every car's curve.</summary>
    public void ResetAll()
    {
        lock (_gate)
        {
            _bins.Clear();
        }
    }
}
