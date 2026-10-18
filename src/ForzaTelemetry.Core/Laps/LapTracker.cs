namespace ForzaTelemetry.Core.Laps;

/// <summary>
/// Detects lap completion, keeps the lap list and computes a live delta against the best lap.
/// </summary>
/// <remarks>
/// <para><b>Lap detection.</b> The packet's <see cref="TelemetryPacket.LapNumber"/> is the number of laps
/// COMPLETED. When it grows, the lap that just ended took <see cref="TelemetryPacket.LastLap"/> seconds
/// (falling back to the last seen <see cref="TelemetryPacket.CurrentLap"/> if the game reports 0).</para>
///
/// <para><b>Live delta algorithm.</b> Comparing "my current time" with "best lap time" is meaningless
/// mid-lap - the question is "where was I when the best lap was at this same spot?". So while driving
/// we record samples of (distance into the lap, time into the lap), at most one per metre of travel, which
/// keeps distance strictly increasing. When a lap finishes faster than every previous one, its samples become
/// the reference. The delta is then <c>currentTime - bestTimeAt(currentDistance)</c>, where bestTimeAt does a
/// binary search over the reference distances and linearly interpolates between the two neighbouring samples.
/// Positive delta = slower than best at this spot.</para>
///
/// <para><b>Validity.</b> A lap only becomes the reference if we saw it from its very start (the first packet
/// of the lap had a small CurrentLap); a lap joined midway has no samples for its first part and would
/// corrupt the delta.</para>
///
/// <para><b>Restart handling.</b> If LapNumber decreases the race restarted: all history is cleared. If
/// CurrentLap drops sharply within the same lap the lap was restarted: its samples are discarded. Packets
/// with IsRaceOn false (menus/pause) are ignored entirely, so pausing neither ends nor corrupts a lap.</para>
///
/// <para><b>Thread-safety.</b> Single writer (<see cref="OnPacket"/>); readers use <see cref="Current"/>,
/// an immutable <see cref="LapSnapshot"/> swapped with <see cref="Volatile.Write{T}(ref T, T)"/>. The
/// per-lap sample lists are private to the writer; the completed-lap list is an immutable array replaced
/// only when a lap finishes.</para>
/// </remarks>
public sealed class LapTracker : IPacketObserver
{
    /// <summary>A lap seen from its start has a CurrentLap below this many seconds on the first packet.</summary>
    private const double FullLapStartToleranceSeconds = 1.0;

    /// <summary>Minimum distance between recorded samples, meters.</summary>
    private const double SampleSpacingMeters = 1.0;

    /// <summary>CurrentLap dropping by more than this within the same lap number means the lap was restarted.</summary>
    private const double RestartDropSeconds = 1.0;

    private LapSnapshot _snapshot = LapSnapshot.Empty;

    // ---- Writer-only state ----
    private bool _hasPrevious;
    private int _lapsCompleted;
    private double _previousCurrentLap;
    private double _lapStartDistance;
    private bool _currentLapValid;
    private List<(double Distance, double Time)> _currentSamples = [];
    private (double Distance, double Time)[] _bestSamples = [];
    private double? _bestSampleLapTime;
    private double? _lastLap;
    private double? _bestLap;
    private LapRecord[] _laps = [];

    /// <summary>The latest published snapshot; safe to read from any thread.</summary>
    public LapSnapshot Current => Volatile.Read(ref _snapshot);

    /// <inheritdoc />
    public void OnPacket(TelemetryPacket packet)
    {
        if (!packet.IsRaceOn || !float.IsFinite(packet.DistanceTraveled) || !float.IsFinite(packet.CurrentLap))
        {
            return;
        }

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
        Publish(packet, currentLap);
    }

    private void StartLap(int lapsCompleted, double distance, double currentLap)
    {
        _lapsCompleted = lapsCompleted;
        _lapStartDistance = distance;
        _currentSamples = [];
        _currentLapValid = currentLap <= FullLapStartToleranceSeconds;
        _previousCurrentLap = currentLap;
    }

    private void ResetSession()
    {
        _laps = [];
        _bestSamples = [];
        _bestSampleLapTime = null;
        _lastLap = null;
        _bestLap = null;
    }

    private void CompleteLap(TelemetryPacket packet)
    {
        double time = packet.LastLap > 0 ? packet.LastLap : _previousCurrentLap;
        if (time <= 0)
        {
            return;
        }

        var isBest = _bestLap is null || time < _bestLap;
        _lastLap = time;
        if (isBest)
        {
            _bestLap = time;
        }

        // Only a lap observed from its start (and long enough to interpolate) can be the delta reference.
        if (_currentLapValid && _currentSamples.Count >= 2 && (_bestSampleLapTime is null || time < _bestSampleLapTime))
        {
            _bestSamples = [.. _currentSamples];
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

        if (_currentSamples.Count == 0 || distanceIntoLap - _currentSamples[^1].Distance >= SampleSpacingMeters)
        {
            _currentSamples.Add((distanceIntoLap, lapTime));
        }
    }

    /// <summary>Best-lap time at a given distance into the lap, by binary search plus linear interpolation.</summary>
    private double? BestTimeAt(double distanceIntoLap)
    {
        var samples = _bestSamples;
        if (samples.Length < 2 || distanceIntoLap < samples[0].Distance || distanceIntoLap > samples[^1].Distance)
        {
            return null; // outside the recorded range of the reference lap
        }

        int low = 0, high = samples.Length - 1;
        while (high - low > 1)
        {
            var mid = (low + high) / 2;
            if (samples[mid].Distance <= distanceIntoLap)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        var a = samples[low];
        var b = samples[high];
        var span = b.Distance - a.Distance;
        return span <= 0 ? a.Time : a.Time + ((distanceIntoLap - a.Distance) / span * (b.Time - a.Time));
    }

    private void Publish(TelemetryPacket packet, double currentLap)
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

        // Prefer the game's own best lap when present; otherwise our computed one.
        double? bestLap = packet.BestLap > 0 ? packet.BestLap : _bestLap;
        double? lastLap = packet.LastLap > 0 ? packet.LastLap : _lastLap;

        Volatile.Write(ref _snapshot, new LapSnapshot(
            _lapsCompleted + 1, currentLap, lastLap, bestLap, delta, _laps));
    }
}
