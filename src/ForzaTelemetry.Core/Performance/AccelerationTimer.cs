namespace ForzaTelemetry.Core.Performance;

/// <summary>
/// Measures 0-100 km/h, 0-200 km/h and the quarter mile (with trap speed) from a stream of packets.
/// </summary>
/// <remarks>
/// <para><b>State machine.</b> Idle -> Armed (race on and speed under 1 km/h) -> Running (first movement,
/// in a forward gear) -> Done (quarter mile and 200 km/h both reached, or the car stopped after the quarter
/// mile). Any of: reverse gear, IsRaceOn false, a game-clock rewind, or coming back to a standstill before
/// the quarter mile is finished ABORTS the run; the previous completed result is kept on screen.</para>
///
/// <para><b>Why interpolation.</b> Packets arrive roughly every 16 ms, so a naive "first packet at or above
/// 100 km/h" is off by up to one packet interval (~0.016 s) - a visible error on a 2.5 s run. Instead we find
/// the two packets that straddle the target, assume speed changes linearly between them, and solve for the
/// fraction of the interval where the target was crossed:
/// <c>t = t0 + (target - v0) / (v1 - v0) * (t1 - t0)</c>. Quarter-mile uses the same idea on
/// DistanceTraveled. Linear interpolation is accurate here because acceleration is nearly constant over 16 ms.</para>
///
/// <para><b>Why game time.</b> We use <see cref="TelemetryPacket.TimestampMS"/> (the game's clock), not the
/// wall clock: UDP jitter and the UI's 20 Hz tick would corrupt wall-clock timing, while the game timestamp is
/// exact. The time zero is the moment speed crosses 1 km/h (also interpolated) so a creeping car does not
/// start the run early, yet launch time is not lost.</para>
///
/// <para><b>Thread-safety.</b> One writer (<see cref="OnPacket"/>, the receive loop) and many readers.
/// The writer holds a lock for the duration of one packet (uncontended, tens of nanoseconds); <see cref="Current"/>
/// takes the same lock and BUILDS an immutable <see cref="AccelerationSnapshot"/> on read from the writer's
/// fields. Building on read means a run in progress allocates one snapshot per UI refresh (~20 Hz) instead of
/// one per packet (~60 Hz), and a reader can never observe a half-updated run. <see cref="Reset"/> only raises
/// a flag that the writer honours on its next packet.</para>
/// </remarks>
public sealed class AccelerationTimer : IPacketObserver
{
    /// <summary>Below this speed (1 km/h, in m/s) the car counts as standing still.</summary>
    private const double StandstillMs = 1.0 / 3.6;

    private const double Target100Ms = 100.0 / 3.6;
    private const double Target200Ms = 200.0 / 3.6;

    /// <summary>A quarter mile in meters.</summary>
    public const double QuarterMileMeters = 402.336;

    private readonly object _gate = new();
    private AccelerationSnapshot _published = AccelerationSnapshot.Empty;
    private int _resetRequested;

    // ---- Writer-only state (never touched by readers) ----
    private AccelerationState _state = AccelerationState.Idle;
    private TelemetryPacket? _previous;
    private AccelerationSnapshot _lastCompleted = AccelerationSnapshot.Empty;
    private double _startTimeMs;
    private double _startDistance;
    private double? _zeroTo100;
    private double? _zeroTo200;
    private double? _quarterTime;
    private double? _quarterTrap;
    private double _latestTimeMs;

    /// <summary>The current snapshot (built on read while running); safe to read from any thread.</summary>
    public AccelerationSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _state == AccelerationState.Running
                    ? new AccelerationSnapshot(
                        AccelerationState.Running,
                        Math.Max(0, (_latestTimeMs - _startTimeMs) / 1000.0),
                        _zeroTo100,
                        _zeroTo200,
                        _quarterTime,
                        _quarterTrap)
                    : _published;
            }
        }
    }

    /// <summary>Asks the timer to forget all results (also the last completed run). Safe from any thread.</summary>
    public void Reset() => Volatile.Write(ref _resetRequested, 1);

    /// <inheritdoc />
    public void OnPacket(TelemetryPacket packet)
    {
        lock (_gate)
        {
            Process(packet);
        }
    }

    private void Process(TelemetryPacket packet)
    {
        if (Interlocked.Exchange(ref _resetRequested, 0) == 1)
        {
            _state = AccelerationState.Idle;
            _lastCompleted = AccelerationSnapshot.Empty;
            ClearRun();
            Publish(AccelerationSnapshot.Empty);
        }

        if (!float.IsFinite(packet.Speed) || !float.IsFinite(packet.DistanceTraveled))
        {
            return;
        }

        var previous = _previous;
        _previous = packet;

        if (!packet.IsRaceOn)
        {
            _previous = null; // no interpolation across a pause/menu gap
            Abort(standstill: false);
            return;
        }

        if (previous is not null && packet.TimestampMS < previous.TimestampMS)
        {
            // The game clock went backwards (restart or documented overflow): the interval is meaningless.
            previous = null;
            Abort(standstill: packet.Speed < StandstillMs);
            if (_state != AccelerationState.Armed)
            {
                return;
            }
        }

        var stationary = packet.Speed < StandstillMs;

        switch (_state)
        {
            case AccelerationState.Idle:
            case AccelerationState.Done:
                if (stationary)
                {
                    EnterArmed();
                }

                break;

            case AccelerationState.Armed:
                if (stationary)
                {
                    break;
                }

                // Movement. Starting needs a forward gear and a previous (stationary) packet to interpolate from.
                if (packet.Gear == 0 || previous is null)
                {
                    _state = AccelerationState.Idle;
                    Publish(_lastCompleted with { State = AccelerationState.Idle });
                    break;
                }

                BeginRun(previous, packet);
                Advance(previous, packet);
                UpdateRunning(packet);
                break;

            case AccelerationState.Running:
                if (packet.Gear == 0)
                {
                    Abort(stationary);
                    break;
                }

                if (stationary)
                {
                    Abort(standstill: true); // keeps whatever was already measured (see Abort)
                    break;
                }

                if (previous is not null)
                {
                    Advance(previous, packet);
                }

                UpdateRunning(packet);
                break;
        }
    }

    private void EnterArmed()
    {
        _state = AccelerationState.Armed;
        Publish(_lastCompleted with { State = AccelerationState.Armed });
    }

    /// <summary>Finds the instant speed crossed 1 km/h between two packets; that is time zero and distance zero.</summary>
    private void BeginRun(TelemetryPacket previous, TelemetryPacket current)
    {
        ClearRun();
        _state = AccelerationState.Running;

        var v0 = (double)previous.Speed;
        var v1 = (double)current.Speed;
        var fraction = v1 > v0 ? Math.Clamp((StandstillMs - v0) / (v1 - v0), 0, 1) : 1;
        var intervalMs = (double)current.TimestampMS - previous.TimestampMS;

        _startTimeMs = previous.TimestampMS + (fraction * intervalMs);
        _startDistance = previous.DistanceTraveled + (fraction * ((double)current.DistanceTraveled - previous.DistanceTraveled));
    }

    /// <summary>Checks whether the interval between two packets contains any of the three measurement lines.</summary>
    private void Advance(TelemetryPacket previous, TelemetryPacket current)
    {
        double intervalMs = (double)current.TimestampMS - previous.TimestampMS;
        if (intervalMs <= 0)
        {
            return;
        }

        double v0 = previous.Speed, v1 = current.Speed;

        if (_zeroTo100 is null && v0 < Target100Ms && v1 >= Target100Ms)
        {
            _zeroTo100 = CrossingTimeSeconds(previous, intervalMs, v0, v1, Target100Ms);
        }

        if (_zeroTo200 is null && v0 < Target200Ms && v1 >= Target200Ms)
        {
            _zeroTo200 = CrossingTimeSeconds(previous, intervalMs, v0, v1, Target200Ms);
        }

        var d0 = previous.DistanceTraveled - _startDistance;
        var d1 = current.DistanceTraveled - _startDistance;
        if (_quarterTime is null && d0 < QuarterMileMeters && d1 >= QuarterMileMeters && d1 > d0)
        {
            var fraction = (QuarterMileMeters - d0) / (d1 - d0);
            _quarterTime = ((previous.TimestampMS + (fraction * intervalMs)) - _startTimeMs) / 1000.0;
            _quarterTrap = (v0 + (fraction * (v1 - v0))) * 3.6;
        }
    }

    private double CrossingTimeSeconds(TelemetryPacket previous, double intervalMs, double v0, double v1, double target)
    {
        var fraction = (target - v0) / (v1 - v0);
        return ((previous.TimestampMS + (fraction * intervalMs)) - _startTimeMs) / 1000.0;
    }

    private void UpdateRunning(TelemetryPacket current)
    {
        if (_quarterTime is not null && _zeroTo200 is not null)
        {
            Finish();
            return;
        }

        // No publish: Current builds the running snapshot from these fields when read.
        _latestTimeMs = current.TimestampMS;
    }

    private void Finish()
    {
        _state = AccelerationState.Done;
        _lastCompleted = new AccelerationSnapshot(
            AccelerationState.Done, 0, _zeroTo100, _zeroTo200, _quarterTime, _quarterTrap);
        Publish(_lastCompleted);
    }

    /// <summary>
    /// Ends a run that cannot continue. If it already measured something (0-100 or the quarter mile), those
    /// partial results are kept as the completed run - a driver who lifts after the line should not lose the
    /// time. A run with nothing measured is dropped and the previous completed result stays.
    /// </summary>
    private void Abort(bool standstill)
    {
        if (_state == AccelerationState.Running)
        {
            if (_zeroTo100 is not null || _zeroTo200 is not null || _quarterTime is not null)
            {
                Finish();
                if (standstill)
                {
                    EnterArmed();
                }

                return;
            }

            ClearRun();
        }

        var next = standstill ? AccelerationState.Armed : AccelerationState.Idle;
        if (_state == next && _published.State == next)
        {
            return;
        }

        _state = next;
        Publish(_lastCompleted with { State = next });
    }

    private void ClearRun()
    {
        _zeroTo100 = null;
        _zeroTo200 = null;
        _quarterTime = null;
        _quarterTrap = null;
    }

    private void Publish(AccelerationSnapshot snapshot) => _published = snapshot;
}
