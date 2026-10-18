namespace ForzaTelemetry.Core.Performance;

/// <summary>
/// Measures 0-100 km/h, 0-200 km/h, 100-200 km/h, 0-60 mph, 60-130 mph and the quarter mile (with trap speed)
/// from a stream of packets. All windows come from ONE run and one interpolation routine.
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
/// one per packet (~60 Hz), and a reader can never observe a half-updated run. <see cref="Reset"/> takes the same
/// lock and clears at once, so a "reset" button empties the display immediately instead of on the next packet (which
/// never comes while the game is paused or the listener is stopped).</para>
/// </remarks>
public sealed class AccelerationTimer : IPacketObserver
{
    /// <summary>Below this speed (1 km/h, in m/s) the car counts as standing still.</summary>
    private const double StandstillMs = SpeedCrossing.StandstillMs;

    /// <summary>A quarter mile in meters.</summary>
    public const double QuarterMileMeters = 402.336;

    // Measurement lines, as one table: index -> target speed (m/s). A run keeps one nullable crossing time
    // per line, so adding a window is one more row, not another copy of the same if-block. 130 mph
    // (209.2 km/h) is above 200 km/h, which is why a run needs an "end" rule besides "all lines reached".
    private const int Line100 = 0;
    private const int Line200 = 1;
    private const int Line60Mph = 2;
    private const int Line130Mph = 3;

    private static readonly double[] LineSpeeds =
    [
        SpeedCrossing.KmhToMs(100),
        SpeedCrossing.KmhToMs(200),
        SpeedCrossing.MphToMs(60),
        SpeedCrossing.MphToMs(130),
    ];

    /// <summary>Speed (m/s) the car must fall below its run maximum to count as "the driver lifted" (5 km/h).</summary>
    private const double LiftDropMs = 5.0 / 3.6;

    /// <summary>Raw throttle (0..255) at or below which the pedal counts as lifted (about 10%).</summary>
    private const byte ThrottleLifted = 25;

    /// <summary>How long (game ms) the throttle must stay lifted to end the run.</summary>
    private const double LiftHoldMs = 500;

    // The lift rule stays OFF until the launch is really under way: once the car passed 30 km/h, or a second went by. A rolling start
    // (already creeping while the throttle is still at zero) must not be read as "the driver lifted" before the pedal was pressed.
    private static readonly double LiftArmSpeedMs = SpeedCrossing.KmhToMs(30);
    private const double LiftArmAfterMs = 1000;

    private readonly object _gate = new();
    private AccelerationSnapshot _published = AccelerationSnapshot.Empty;

    // ---- Writer-only state (never touched by readers) ----
    private AccelerationState _state = AccelerationState.Idle;
    private TelemetryPacket? _previous;
    private AccelerationSnapshot _lastCompleted = AccelerationSnapshot.Empty;
    private double _startTimeMs;
    private double _startDistance;
    private readonly double?[] _crossings = new double?[LineSpeeds.Length]; // seconds since run start, per line
    private double _maxSpeedMs;
    private bool _liftRuleArmed;
    private double? _throttleLiftedSinceMs;
    private long _completedRuns; // never reset: readers compare it with the last value they saw
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
                    ? Build(AccelerationState.Running, Math.Max(0, (_latestTimeMs - _startTimeMs) / 1000.0))
                    : _published;
            }
        }
    }

    /// <summary>Forgets all results (also the last completed run) and any run in progress. Safe from any thread.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _state = AccelerationState.Idle;
            _lastCompleted = AccelerationSnapshot.Empty with { CompletedRuns = _completedRuns };
            _previous = null; // never interpolate across the reset
            ClearRun();
            Publish(_lastCompleted);
        }
    }

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

        _startTimeMs = SpeedCrossing.TimeMs(previous, current, StandstillMs);
        _startDistance = SpeedCrossing.Distance(previous, current, StandstillMs);
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

        for (var i = 0; i < LineSpeeds.Length; i++)
        {
            var target = LineSpeeds[i];
            if (_crossings[i] is null && v0 < target && v1 >= target)
            {
                _crossings[i] = (SpeedCrossing.TimeMs(previous, current, target) - _startTimeMs) / 1000.0;
            }
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

    private void UpdateRunning(TelemetryPacket current)
    {
        if (_quarterTime is not null && _crossings[Line200] is not null && _crossings[Line130Mph] is not null)
        {
            Finish();
            return;
        }

        _maxSpeedMs = Math.Max(_maxSpeedMs, current.Speed);

        _liftRuleArmed |= current.Speed >= LiftArmSpeedMs || current.TimestampMS - _startTimeMs >= LiftArmAfterMs;
        if (!_liftRuleArmed)
        {
            _latestTimeMs = current.TimestampMS;
            return; // still launching: neither a low throttle nor a falling speed means "lifted" yet
        }

        // "The driver lifted" ends the run so windows that can never be reached (a car topping out between
        // 200 km/h and 130 mph) do not keep it open for minutes and later record a bogus late crossing.
        // Either speed falls 5 km/h under the run's peak, or the throttle stays lifted for half a second of
        // GAME time (a gear change cuts the throttle for far less than that).
        var lifted = _maxSpeedMs - current.Speed >= LiftDropMs;
        if (current.Accel <= ThrottleLifted)
        {
            _throttleLiftedSinceMs ??= current.TimestampMS;
            lifted |= current.TimestampMS - _throttleLiftedSinceMs >= LiftHoldMs;
        }
        else
        {
            _throttleLiftedSinceMs = null;
        }

        if (lifted)
        {
            Abort(standstill: false); // keeps the run only if a window the pages show was measured (see Abort)
            return;
        }

        // No publish: Current builds the running snapshot from these fields when read.
        _latestTimeMs = current.TimestampMS;
    }

    private void Finish()
    {
        _completedRuns++;
        _state = AccelerationState.Done;
        _lastCompleted = Build(AccelerationState.Done, 0);
        Publish(_lastCompleted);
    }

    /// <summary>
    /// Ends a run that cannot continue. If a window the pages show was measured (0-100 km/h, 0-60 mph or the quarter mile),
    /// the partial results are kept as the completed run - a driver who lifts after the line should not lose the time, and
    /// a mph player's 0-60 (done below 100 km/h) or a slow car's quarter mile is a real result. Otherwise the run is dropped
    /// and the previous completed result stays.
    /// <para>
    /// A kept partial shows THIS run's values (nulls for what it did not reach); it does not wipe older bests, because those
    /// live in the per-car history, which merges field by field and only ever improves a value it has.
    /// </para>
    /// </summary>
    private void Abort(bool standstill)
    {
        if (_state == AccelerationState.Running)
        {
            if (_crossings[Line100] is not null || _crossings[Line60Mph] is not null || _quarterTime is not null)
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
        Array.Clear(_crossings);
        _maxSpeedMs = 0;
        _liftRuleArmed = false;
        _throttleLiftedSinceMs = null;
        _quarterTime = null;
        _quarterTrap = null;
    }

    /// <summary>
    /// Builds a snapshot from the run's raw crossing times. Windows that start above 0 km/h (100-200 km/h,
    /// 60-130 mph) are DIFFERENCES of two crossings of the same run, so they need no clock of their own:
    /// the common start offset cancels out.
    /// </summary>
    private AccelerationSnapshot Build(AccelerationState state, double elapsedSeconds) => new(
        state,
        elapsedSeconds,
        _crossings[Line100],
        _crossings[Line200],
        _quarterTime,
        _quarterTrap,
        HundredTo200KmhSeconds: Window(_crossings[Line100], _crossings[Line200]),
        ZeroTo60MphSeconds: _crossings[Line60Mph],
        SixtyTo130MphSeconds: Window(_crossings[Line60Mph], _crossings[Line130Mph]),
        CompletedRuns: _completedRuns);

    private static double? Window(double? from, double? to) => from is not null && to is not null ? to - from : null;

    private void Publish(AccelerationSnapshot snapshot) => _published = snapshot;
}
