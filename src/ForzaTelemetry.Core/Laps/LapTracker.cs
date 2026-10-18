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
/// of the lap had a small CurrentLap), recorded all of it, and the lap counter advanced by exactly one: when it jumps by
/// more, the samples in hand belong to a lap that is not the one the packet reports, so none is promoted.</para>
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

    private readonly object _gate = new();

    // ---- Writer-owned state (scalars below are read by Current under _gate) ----
    // Lap boundaries and sample spacing are decided by the detector the lap trace recorder uses too (see LapBoundaryDetector).
    private readonly LapBoundaryDetector _boundary = new();
    private LapPoint[] _current = new LapPoint[MaxSamplesPerLap];
    private int _currentCount;
    private LapPoint[] _best = new LapPoint[MaxSamplesPerLap];
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
                return _boundary.HasPrevious
                    ? new LapSnapshot(_boundary.LapsCompleted + 1, _shownCurrentLap, _shownLastLap, _shownBestLap, _shownDelta, _laps)
                    : LapSnapshot.Empty;
            }
        }
    }

    /// <summary>
    /// Forgets the whole session: the lap list, best/last and the delta reference, as if no packet had ever arrived.
    /// Takes the writer's lock, so it is safe from any thread and empties the display immediately.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _boundary.Reset();
            _currentCount = 0;
            ResetSession(); // lap list, best/last and the reference samples
            _shownCurrentLap = 0;
            _shownLastLap = null;
            _shownBestLap = null;
            _shownDelta = null;
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

        switch (_boundary.Classify(packet))
        {
            case LapBoundary.RaceRestarted:
                ResetSession();
                StartLap(packet, currentLap);
                break;
            case LapBoundary.LapCompleted:
                CompleteLap(packet);
                StartLap(packet, currentLap);
                break;
            case LapBoundary.FirstPacket:
            case LapBoundary.LapRestarted: // same lap number but the lap clock jumped back: the player restarted the lap
                StartLap(packet, currentLap);
                break;
        }

        _boundary.Advance(currentLap);
        RecordSample(packet.DistanceTraveled, currentLap);
        UpdateShown(packet, currentLap);
    }

    private void StartLap(TelemetryPacket packet, double currentLap)
    {
        _boundary.StartLap(packet.LapNumber, packet.DistanceTraveled, currentLap);
        _currentCount = 0; // reuse the preallocated arrays
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
        var finished = _boundary.Finish(packet);
        var time = finished.Time;

        // The packet's LapNumber is the number of laps COMPLETED, so the lap that just ended is lap number LapNumber. Taking
        // it from the packet (not from "my previous count + 1") keeps the list in step with CurrentLapNumber in two cases:
        // this tracker joined a race that was already on lap 4 (the hero says "Lap 5" and the lap it finishes is listed as 5, not 1),
        // and the counter jumped by more than one between two packets (a dropped burst, a pause: 3 -> 5). In that jump the one
        // lap we can describe is the last one (5): its time is LastLap. Lap 4 is NOT invented: we never saw it, and a made-up
        // row with an unknown time would claim knowledge we do not have.
        var finishedLapNumber = packet.LapNumber;

        if (time <= 0)
        {
            // Unknown duration: list the lap anyway (time 0 = unknown) so numbering stays aligned with
            // LapNumber, but never let it become best/last or a delta reference.
            _laps = [.. _laps, new LapRecord(finishedLapNumber, 0, false)];
            return;
        }

        var isBest = _bestLap is null || time < _bestLap;
        _lastLap = time;
        if (isBest)
        {
            _bestLap = time;
        }

        // Only a lap observed from its start (and long enough to interpolate), and whose samples really are the reported lap's,
        // can be the delta reference.
        if (_boundary.CanBeReference(finished, _currentCount) && (_bestSampleLapTime is null || time < _bestSampleLapTime))
        {
            // Swap instead of copy: the old reference array becomes the scratch space for the next lap.
            (_best, _current) = (_current, _best);
            _bestCount = _currentCount;
            _bestSampleLapTime = time;
        }

        // The array is replaced, never mutated, so snapshots holding the old one stay valid.
        _laps = [.. _laps, new LapRecord(finishedLapNumber, time, isBest)];
    }

    private void RecordSample(double distance, double lapTime)
    {
        var lastDistance = _currentCount > 0 ? _current[_currentCount - 1].Distance : 0;
        if (!_boundary.ShouldRecord(distance, _currentCount, lastDistance))
        {
            return;
        }

        _current[_currentCount] = new LapPoint(distance - _boundary.LapStartDistance, lapTime);
        _currentCount++;
    }

    /// <summary>Best-lap time at a given distance into the lap, by the interpolation shared with the lap trace.</summary>
    private double? BestTimeAt(double distanceIntoLap)
    {
        if (!LapInterpolation.TryLocate(_best.AsSpan(0, _bestCount), distanceIntoLap, out var low, out var high, out var t))
        {
            return null; // outside the recorded range of the reference lap
        }

        return _best[low].Time + (t * (_best[high].Time - _best[low].Time));
    }

    private void UpdateShown(TelemetryPacket packet, double currentLap)
    {
        double? delta = null;
        if (_boundary.CurrentLapValid)
        {
            var best = BestTimeAt(packet.DistanceTraveled - _boundary.LapStartDistance);
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
