namespace ForzaTelemetry.Core.Laps;

/// <summary>
/// Detects lap completion, keeps the lap list and computes a live delta against the best lap.
/// </summary>
/// <remarks>
/// <para><b>Lap detection.</b> The packet's <see cref="TelemetryPacket.LapNumber"/> is the number of laps
/// COMPLETED. When it grows, the lap that just ended took <see cref="TelemetryPacket.LastLap"/> seconds
/// (falling back to the last seen <see cref="TelemetryPacket.CurrentLap"/> if the game reports 0). If neither
/// is usable the lap is still listed, with <c>TimeSeconds = 0</c> meaning "unknown", so lap numbering never
/// skips.</para>
///
/// <para><b>Live delta algorithm.</b> Comparing "my current time" with "best lap time" is meaningless
/// mid-lap - the question is "where was I when the best lap was at this same spot?". So while driving
/// we record samples of (distance into the lap, time into the lap), at most one per metre of travel, which
/// keeps distance strictly increasing. When a lap finishes faster than every previous one, its samples become
/// the reference. The delta is then <c>currentTime - bestTimeAt(currentDistance)</c>, where bestTimeAt does a
/// binary search over the reference distances and linearly interpolates between the two neighbouring samples.
/// Positive delta = slower than best at this spot.</para>
///
/// <para><b>Bounded memory.</b> Samples live in two preallocated arrays of <see cref="MaxSamplesPerLap"/>
/// entries (current lap and reference lap). Promoting a lap to reference just swaps the two arrays, so the hot
/// path never allocates or grows a list. In free roam the lap number never changes; once the array is full the
/// lap stops recording and is marked unusable as a reference (it is not a real lap anyway).</para>
///
/// <para><b>Validity.</b> A lap only becomes the reference if we saw it from its very start (the first packet
/// of the lap had a small CurrentLap) and recorded all of it.</para>
///
/// <para><b>Restart handling.</b> If LapNumber decreases the race restarted: all history is cleared. If
/// CurrentLap drops sharply within the same lap the lap was restarted: its samples are discarded. Packets
/// with IsRaceOn false (menus/pause) are ignored entirely, so pausing neither ends nor corrupts a lap.</para>
///
/// <para><b>Thread-safety.</b> Single writer (<see cref="OnPacket"/>) and readers on <see cref="Current"/>.
/// The writer updates a few scalar fields under a lock per packet; <see cref="Current"/> takes the same lock
/// and BUILDS an immutable <see cref="LapSnapshot"/> on read. That costs one small allocation per UI refresh
/// (~20 Hz) rather than one per packet (~60 Hz). The sample arrays are touched only by the writer; the
/// completed-lap list is an immutable array replaced only when a lap finishes.</para>
/// </remarks>
public sealed class LapTracker : IPacketObserver
{
    /// <summary>Capacity of the per-lap sample arrays: one sample per metre, so laps up to 6 km are fully recorded.</summary>
    public const int MaxSamplesPerLap = 6000;

    /// <summary>A lap seen from its start has a CurrentLap below this many seconds on the first packet.</summary>
    private const double FullLapStartToleranceSeconds = 1.0;

    /// <summary>Minimum distance between recorded samples, meters.</summary>
    private const double SampleSpacingMeters = 1.0;

    /// <summary>CurrentLap dropping by more than this within the same lap number means the lap was restarted.</summary>
    private const double RestartDropSeconds = 1.0;

    private readonly object _gate = new();

    // ---- Writer-owned state (scalars below are read by Current under _gate) ----
    private bool _hasPrevious;
    private int _lapsCompleted;
    private double _previousCurrentLap;
    private double _lapStartDistance;
    private bool _currentLapValid;
    private double[] _currentDistances = new double[MaxSamplesPerLap];
    private double[] _currentTimes = new double[MaxSamplesPerLap];
    private int _currentCount;
    private double[] _bestDistances = new double[MaxSamplesPerLap];
    private double[] _bestTimes = new double[MaxSamplesPerLap];
    private int _bestCount;
    private double? _bestSampleLapTime;
    private double? _lastLap;
    private double? _bestLap;
    private LapRecord[] _laps = [];

    // Values the snapshot shows, refreshed per packet.
    private double _shownCurrentLap;
    private double? _shownLastLap;
    private double? _shownBestLap;
    private double? _shownDelta;

    /// <summary>The current snapshot, built on read; safe to read from any thread.</summary>
    public LapSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _hasPrevious
                    ? new LapSnapshot(_lapsCompleted + 1, _shownCurrentLap, _shownLastLap, _shownBestLap, _shownDelta, _laps)
                    : LapSnapshot.Empty;
            }
        }
    }

    /// <inheritdoc />
    public void OnPacket(TelemetryPacket packet)
    {
        if (!packet.IsRaceOn || !float.IsFinite(packet.DistanceTraveled) || !float.IsFinite(packet.CurrentLap))
        {
            return;
        }

        lock (_gate)
        {
            Process(packet);
        }
    }

    private void Process(TelemetryPacket packet)
    {
        double currentLap = packet.CurrentLap;

        if (!_hasPrevious)
        {
            StartLap(packet.LapNumber, packet.DistanceTraveled, currentLap);
            _hasPrevious = true;
        }
        else if (packet.LapNumber < _lapsCompleted)
        {
            ResetSession();
            StartLap(packet.LapNumber, packet.DistanceTraveled, currentLap);
        }
        else if (packet.LapNumber > _lapsCompleted)
        {
            CompleteLap(packet);
            StartLap(packet.LapNumber, packet.DistanceTraveled, currentLap);
        }
        else if (currentLap < _previousCurrentLap - RestartDropSeconds)
        {
            // Same lap number but the lap clock jumped back: the player restarted the lap.
            StartLap(packet.LapNumber, packet.DistanceTraveled, currentLap);
        }

        _previousCurrentLap = currentLap;
        RecordSample(packet.DistanceTraveled, currentLap);
        UpdateShown(packet, currentLap);
    }

    private void StartLap(int lapsCompleted, double distance, double currentLap)
    {
        _lapsCompleted = lapsCompleted;
        _lapStartDistance = distance;
        _currentCount = 0; // reuse the preallocated arrays
        _currentLapValid = currentLap <= FullLapStartToleranceSeconds;
        _previousCurrentLap = currentLap;
    }

    private void ResetSession()
    {
        _laps = [];
        _bestCount = 0;
        _bestSampleLapTime = null;
        _lastLap = null;
        _bestLap = null;
    }

    private void CompleteLap(TelemetryPacket packet)
    {
        double time = packet.LastLap > 0 ? packet.LastLap : _previousCurrentLap;

        if (time <= 0)
        {
            // Unknown duration: list the lap anyway (time 0 = unknown) so numbering stays aligned with
            // LapNumber, but never let it become best/last or a delta reference.
            _laps = [.. _laps, new LapRecord(_laps.Length + 1, 0, false)];
            return;
        }

        var isBest = _bestLap is null || time < _bestLap;
        _lastLap = time;
        if (isBest)
        {
            _bestLap = time;
        }

        // Only a lap observed from its start (and long enough to interpolate) can be the delta reference.
        if (_currentLapValid && _currentCount >= 2 && (_bestSampleLapTime is null || time < _bestSampleLapTime))
        {
            // Swap instead of copy: the old reference arrays become the scratch space for the next lap.
            (_bestDistances, _currentDistances) = (_currentDistances, _bestDistances);
            (_bestTimes, _currentTimes) = (_currentTimes, _bestTimes);
            _bestCount = _currentCount;
            _bestSampleLapTime = time;
        }

        // The array is replaced, never mutated, so snapshots holding the old one stay valid.
        _laps = [.. _laps, new LapRecord(_laps.Length + 1, time, isBest)];
    }

    private void RecordSample(double distance, double lapTime)
    {
        var distanceIntoLap = distance - _lapStartDistance;
        if (distanceIntoLap < 0)
        {
            return;
        }

        if (_currentCount > 0 && distanceIntoLap - _currentDistances[_currentCount - 1] < SampleSpacingMeters)
        {
            return;
        }

        if (_currentCount >= MaxSamplesPerLap)
        {
            _currentLapValid = false; // free roam / absurdly long lap: stop recording, never a reference
            return;
        }

        _currentDistances[_currentCount] = distanceIntoLap;
        _currentTimes[_currentCount] = lapTime;
        _currentCount++;
    }

    /// <summary>Best-lap time at a given distance into the lap, by binary search plus linear interpolation.</summary>
    private double? BestTimeAt(double distanceIntoLap)
    {
        if (_bestCount < 2 || distanceIntoLap < _bestDistances[0] || distanceIntoLap > _bestDistances[_bestCount - 1])
        {
            return null; // outside the recorded range of the reference lap
        }

        int low = 0, high = _bestCount - 1;
        while (high - low > 1)
        {
            var mid = (low + high) / 2;
            if (_bestDistances[mid] <= distanceIntoLap)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        var span = _bestDistances[high] - _bestDistances[low];
        return span <= 0
            ? _bestTimes[low]
            : _bestTimes[low] + ((distanceIntoLap - _bestDistances[low]) / span * (_bestTimes[high] - _bestTimes[low]));
    }

    private void UpdateShown(TelemetryPacket packet, double currentLap)
    {
        double? delta = null;
        if (_currentLapValid)
        {
            var best = BestTimeAt(packet.DistanceTraveled - _lapStartDistance);
            if (best is not null)
            {
                delta = currentLap - best;
            }
        }

        _shownCurrentLap = currentLap;
        _shownDelta = delta;
        // Prefer the game's own best/last lap when present; otherwise our computed ones.
        _shownBestLap = packet.BestLap > 0 ? packet.BestLap : _bestLap;
        _shownLastLap = packet.LastLap > 0 ? packet.LastLap : _lastLap;
    }
}
