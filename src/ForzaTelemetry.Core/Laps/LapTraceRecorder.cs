namespace ForzaTelemetry.Core.Laps;

/// <summary>What a reader needs to know about a <see cref="LapTraceRecorder"/> before it copies any samples.</summary>
/// <param name="CurrentCount">Samples recorded so far in the lap in progress.</param>
/// <param name="CurrentVersion">Changes whenever the current lap's samples changed (a new sample, a new lap, a reset): the reader's "copy again" signal.</param>
/// <param name="CurrentIsComparable">True when the lap in progress was seen from its start line, so its distances line up with the best lap's. False after joining mid-lap or in free roam.</param>
/// <param name="CurrentLapNumber">1-based number of the lap in progress (the game's laps completed + 1).</param>
/// <param name="BestCount">Samples of the best complete lap; 0 when there is none yet.</param>
/// <param name="BestVersion">Changes whenever the best lap's samples were replaced (a faster lap, a reset).</param>
/// <param name="BestLapSeconds">Lap time of the best complete lap; null when there is none.</param>
/// <param name="BestLapNumber">The game's number of that lap (0 when there is none).</param>
public readonly record struct LapTraceStatus(
    int CurrentCount,
    int CurrentVersion,
    bool CurrentIsComparable,
    int CurrentLapNumber,
    int BestCount,
    int BestVersion,
    double? BestLapSeconds,
    int BestLapNumber);

/// <summary>
/// Records, for the lap in progress and for the best COMPLETE lap, what the car was doing at each metre of the lap
/// (speed, throttle, brake, steering, gear and the lap clock), so a screen can lay the two laps over each other by distance.
/// </summary>
/// <remarks>
/// <para><b>Why a second recorder next to <see cref="LapTracker"/>.</b> The tracker keeps only (distance, time) pairs, which is
/// all a live delta needs; a comparison chart needs the driving inputs as well, and carrying them there would turn the
/// tracker's hot path into something much heavier for every user, including those who never open the chart. The two follow the
/// SAME rules (shared constants, the same lap-start/skipped-lap/restart logic), so the lap this recorder calls "best" is the lap
/// the tracker's delta compares against.</para>
///
/// <para><b>Validity.</b> A lap becomes the best trace only if it was seen from its start (the first packet of the lap had a
/// small lap clock), was fully recorded, ended with the lap counter advancing by exactly one (a bigger jump means the samples
/// in hand are not the reported lap's), and has a known time faster than the previous best trace. Packets with
/// <c>IsRaceOn</c> false (menus, pause) are ignored, and a race restart (the lap counter going down) forgets everything.</para>
///
/// <para><b>Bounded memory, no per-packet allocation.</b> Two preallocated arrays of <see cref="MaxSamplesPerLap"/> samples
/// (one sample per metre, so laps up to 6 km are complete). Promoting a lap swaps the arrays instead of copying. In free roam
/// the lap number never changes: once the array is full the lap stops recording and is never a reference.</para>
///
/// <para><b>Thread-safety.</b> One writer (<see cref="OnPacket"/>, the receive loop), readers on the UI thread. Everything is
/// under one short lock; readers copy into arrays THEY own (<see cref="CopyCurrentTo"/>, <see cref="CopyBestTo"/>) and use the
/// versions in <see cref="Status"/> to copy only what changed, so the 20 Hz refresh never allocates and the best lap (which
/// changes a few times per session) is not copied on every tick.</para>
/// </remarks>
public sealed class LapTraceRecorder : IPacketObserver
{
    /// <summary>Capacity of each lap's sample array: one sample per metre, so laps up to 6 km are fully recorded.</summary>
    public const int MaxSamplesPerLap = LapTracker.MaxSamplesPerLap;

    private readonly object _gate = new();

    private bool _hasPrevious;
    private int _lapsCompleted;
    private double _previousCurrentLap;
    private double _lapStartDistance;
    private bool _currentLapValid;
    private LapTraceSample[] _current = new LapTraceSample[MaxSamplesPerLap];
    private int _currentCount;
    private int _currentVersion;
    private LapTraceSample[] _best = new LapTraceSample[MaxSamplesPerLap];
    private int _bestCount;
    private int _bestVersion;
    private double? _bestLapSeconds;
    private int _bestLapNumber;

    /// <summary>A cheap picture of both laps, for deciding what to copy. Safe from any thread.</summary>
    public LapTraceStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new LapTraceStatus(
                    _currentCount, _currentVersion, _hasPrevious && _currentLapValid, _lapsCompleted + 1,
                    _bestCount, _bestVersion, _bestLapSeconds, _bestLapNumber);
            }
        }
    }

    /// <summary>
    /// Copies the lap in progress (nearest the start first) into <paramref name="destination"/>; if it is smaller than the
    /// recorded count only the first <c>destination.Length</c> samples are copied.
    /// </summary>
    /// <returns>How many samples were written.</returns>
    public int CopyCurrentTo(Span<LapTraceSample> destination)
    {
        lock (_gate)
        {
            var n = Math.Min(_currentCount, destination.Length);
            _current.AsSpan(0, n).CopyTo(destination);
            return n;
        }
    }

    /// <summary>Copies the best complete lap into <paramref name="destination"/> (see <see cref="CopyCurrentTo"/>).</summary>
    /// <returns>How many samples were written; 0 when there is no best lap yet.</returns>
    public int CopyBestTo(Span<LapTraceSample> destination)
    {
        lock (_gate)
        {
            var n = Math.Min(_bestCount, destination.Length);
            _best.AsSpan(0, n).CopyTo(destination);
            return n;
        }
    }

    /// <summary>
    /// Forgets both laps, as if no packet had ever arrived. Takes the writer's lock, so it is safe from any thread, but a
    /// session reset belongs BEFORE the receive loop starts (see <c>TelemetryEngines.BeginSession</c>).
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _hasPrevious = false;
            _lapsCompleted = 0;
            _previousCurrentLap = 0;
            _lapStartDistance = 0;
            _currentLapValid = false;
            _currentCount = 0;
            _currentVersion++;
            ForgetBest();
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
            ForgetBest(); // the race restarted
            StartLap(packet.LapNumber, packet.DistanceTraveled, currentLap);
        }
        else if (packet.LapNumber > _lapsCompleted)
        {
            CompleteLap(packet);
            StartLap(packet.LapNumber, packet.DistanceTraveled, currentLap);
        }
        else if (currentLap < _previousCurrentLap - LapTracker.RestartDropSeconds)
        {
            // Same lap number but the lap clock jumped back: the player restarted the lap.
            StartLap(packet.LapNumber, packet.DistanceTraveled, currentLap);
        }

        _previousCurrentLap = currentLap;
        Record(packet, currentLap);
    }

    private void StartLap(int lapsCompleted, double distance, double currentLap)
    {
        _lapsCompleted = lapsCompleted;
        _lapStartDistance = distance;
        _currentCount = 0; // reuse the preallocated array
        _currentVersion++;
        _currentLapValid = currentLap <= LapTracker.FullLapStartToleranceSeconds;
        _previousCurrentLap = currentLap;
    }

    private void ForgetBest()
    {
        _bestCount = 0;
        _bestLapSeconds = null;
        _bestLapNumber = 0;
        _bestVersion++;
    }

    private void CompleteLap(TelemetryPacket packet)
    {
        // Same reasoning as LapTracker.CompleteLap: when more than one lap completed between two packets the samples in hand are
        // not the reported lap's, so none may become a reference (and the lap in progress is no fallback for its time either).
        var skippedLaps = packet.LapNumber - _lapsCompleted > 1;
        double time = packet.LastLap > 0 ? packet.LastLap : skippedLaps ? 0 : _previousCurrentLap;

        if (skippedLaps || !_currentLapValid || _currentCount < 2 || time <= 0 || (_bestLapSeconds is { } best && time >= best))
        {
            return;
        }

        // Swap instead of copy: the old best array becomes the scratch space for the next lap.
        (_best, _current) = (_current, _best);
        _bestCount = _currentCount;
        _bestLapSeconds = time;
        _bestLapNumber = packet.LapNumber;
        _bestVersion++;
    }

    private void Record(TelemetryPacket packet, double lapTime)
    {
        var distanceIntoLap = packet.DistanceTraveled - _lapStartDistance;
        if (distanceIntoLap < 0)
        {
            return;
        }

        if (_currentCount > 0 && distanceIntoLap - _current[_currentCount - 1].DistanceMeters < LapTracker.SampleSpacingMeters)
        {
            return;
        }

        if (_currentCount >= MaxSamplesPerLap)
        {
            _currentLapValid = false; // free roam / absurdly long lap: stop recording, never a reference
            return;
        }

        _current[_currentCount] = new LapTraceSample
        {
            DistanceMeters = (float)distanceIntoLap,
            TimeSeconds = (float)lapTime,
            SpeedMs = packet.Speed,
            Throttle = packet.Accel / 255f,
            Brake = packet.Brake / 255f,
            Steer = Math.Clamp(packet.Steer / 127f, -1f, 1f),
            Gear = packet.Gear,
        };
        _currentCount++;
        _currentVersion++;
    }
}
