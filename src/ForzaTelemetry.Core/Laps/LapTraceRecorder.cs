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
/// <param name="CurrentLapId">Identifies the lap in progress: changes whenever a lap starts (a new lap, a lap restart, a reset). Samples with the same id only ever get appended, so a reader that has copied the first N may copy just the new ones.</param>
public readonly record struct LapTraceStatus(
    int CurrentCount,
    int CurrentVersion,
    bool CurrentIsComparable,
    int CurrentLapNumber,
    int BestCount,
    int BestVersion,
    double? BestLapSeconds,
    int BestLapNumber,
    int CurrentLapId);

/// <summary>
/// Records, for the lap in progress and for the best COMPLETE lap, what the car was doing at each metre of the lap
/// (speed, throttle, brake, steering, gear and the lap clock), so a screen can lay the two laps over each other by distance.
/// </summary>
/// <remarks>
/// <para><b>Why a second recorder next to <see cref="LapTracker"/>.</b> The tracker keeps only (distance, time) pairs, which is
/// all a live delta needs; a comparison chart needs the driving inputs as well, and carrying them there would turn the
/// tracker's hot path into something much heavier for every user, including those who never open the chart. The two follow the
/// SAME rules (one <see cref="LapBoundaryDetector"/> decides lap starts, skipped laps, restarts and sample spacing for both), so
/// the lap this recorder calls "best" is the lap the tracker's delta compares against.</para>
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
/// under one short lock; readers copy into arrays THEY own (<see cref="CopyCurrentTailTo"/>, <see cref="CopyBestTo"/>) and use
/// the versions in <see cref="Status"/> to copy only what changed, so the 20 Hz refresh never allocates, the best lap (which
/// changes a few times per session) is not copied on every tick, and the lap in progress is copied only from where the reader
/// left off.</para>
/// </remarks>
public sealed class LapTraceRecorder : IPacketObserver
{
    /// <summary>Capacity of each lap's sample array: one sample per metre, so laps up to 6 km are fully recorded.</summary>
    public const int MaxSamplesPerLap = LapTracker.MaxSamplesPerLap;

    private readonly object _gate = new();

    private readonly LapBoundaryDetector _boundary = new();
    private LapTraceSample[] _current = new LapTraceSample[MaxSamplesPerLap];
    private int _currentCount;
    private int _currentVersion;
    private int _currentLapId;
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
                    _currentCount, _currentVersion, _boundary.HasPrevious && _boundary.CurrentLapValid, _boundary.LapsCompleted + 1,
                    _bestCount, _bestVersion, _bestLapSeconds, _bestLapNumber, _currentLapId);
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

    /// <summary>
    /// Copies the samples of the lap in progress from index <paramref name="fromIndex"/> on (the ones recorded since the reader
    /// last copied) into <paramref name="destination"/>, which starts at that same index in the reader's array. If it is smaller
    /// than what is left, only what fits is copied.
    /// </summary>
    /// <param name="lapId">The <see cref="LapTraceStatus.CurrentLapId"/> the reader's first <paramref name="fromIndex"/> samples belong to.</param>
    /// <param name="fromIndex">How many samples of that lap the reader already has.</param>
    /// <param name="destination">Room for the new samples.</param>
    /// <returns>How many samples were written; -1 when the lap in progress is no longer lap <paramref name="lapId"/> (the reader must copy the lap from its start).</returns>
    public int CopyCurrentTailTo(int lapId, int fromIndex, Span<LapTraceSample> destination)
    {
        lock (_gate)
        {
            // Checked under the same lock as the copy: a lap that started between the reader's Status and this call must not
            // have its first samples pasted after the previous lap's.
            if (lapId != _currentLapId || fromIndex < 0 || fromIndex > _currentCount)
            {
                return -1;
            }

            var n = Math.Min(_currentCount - fromIndex, destination.Length);
            _current.AsSpan(fromIndex, n).CopyTo(destination);
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
            _boundary.Reset();
            _currentCount = 0;
            _currentVersion++;
            _currentLapId++;
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

        switch (_boundary.Classify(packet))
        {
            case LapBoundary.RaceRestarted:
                ForgetBest();
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
        Record(packet, currentLap);
    }

    private void StartLap(TelemetryPacket packet, double currentLap)
    {
        _boundary.StartLap(packet.LapNumber, packet.DistanceTraveled, currentLap);
        _currentCount = 0; // reuse the preallocated array
        _currentVersion++;
        _currentLapId++; // a reader's partial copy of the old lap is void
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
        // The same rules as LapTracker.CompleteLap (they share the detector): a skipped lap or a lap not seen from its start is
        // never a reference, and the lap in progress is no fallback for a skipped lap's time.
        var finished = _boundary.Finish(packet);

        if (!_boundary.CanBeReference(finished, _currentCount) || (_bestLapSeconds is { } best && finished.Time >= best))
        {
            return;
        }

        // Swap instead of copy: the old best array becomes the scratch space for the next lap.
        (_best, _current) = (_current, _best);
        _bestCount = _currentCount;
        _bestLapSeconds = finished.Time;
        _bestLapNumber = packet.LapNumber;
        _bestVersion++;
    }

    private void Record(TelemetryPacket packet, double lapTime)
    {
        var lastDistance = _currentCount > 0 ? _current[_currentCount - 1].DistanceMeters : 0;
        if (!_boundary.ShouldRecord(packet.DistanceTraveled, _currentCount, lastDistance))
        {
            return;
        }

        _current[_currentCount] = new LapTraceSample
        {
            DistanceMeters = (float)(packet.DistanceTraveled - _boundary.LapStartDistance),
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
