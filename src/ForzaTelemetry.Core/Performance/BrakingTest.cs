namespace ForzaTelemetry.Core.Performance;

/// <summary>Where the <see cref="BrakingTest"/> state machine currently is.</summary>
public enum BrakingState
{
    /// <summary>Not ready: below the arming speed, or a run was aborted. Speeding up past the arming speed re-arms.</summary>
    Idle,

    /// <summary>Fast enough (and racing) that braking hard through a start line will start a measurement.</summary>
    Armed,

    /// <summary>A stop is being measured.</summary>
    Braking,

    /// <summary>The car stopped; results are final until the next run starts.</summary>
    Done,
}

/// <summary>An immutable picture of the braking test. Null results mean "not measured (yet)".</summary>
/// <param name="State">Current state of the machine.</param>
/// <param name="ElapsedSeconds">Seconds since the start-line crossing while <see cref="BrakingState.Braking"/>; 0 otherwise.</param>
/// <param name="HundredToZeroMeters">Distance covered braking from 100 km/h to a stop.</param>
/// <param name="HundredToZeroSeconds">Time taken braking from 100 km/h to a stop.</param>
/// <param name="SixtyToZeroMphMeters">Distance covered braking from 60 mph (96.56 km/h) to a stop.</param>
/// <param name="SixtyToZeroMphSeconds">Time taken braking from 60 mph to a stop.</param>
/// <param name="CompletedRuns">How many stops the test has finished since it was created; it only ever grows (not even
/// <see cref="BrakingTest.Reset"/> lowers it). A reader that remembers the last value it saw knows exactly how many stops
/// completed since it looked, however fast the state moved Braking, Done, Armed, Braking and whatever the values were.</param>
public sealed record BrakingSnapshot(
    BrakingState State,
    double ElapsedSeconds,
    double? HundredToZeroMeters,
    double? HundredToZeroSeconds,
    double? SixtyToZeroMphMeters,
    double? SixtyToZeroMphSeconds,
    long CompletedRuns = 0)
{
    /// <summary>The initial snapshot: idle, nothing measured.</summary>
    public static BrakingSnapshot Empty { get; } = new(BrakingState.Idle, 0, null, null, null, null);

    /// <summary>
    /// While <see cref="BrakingState.Braking"/> the result fields are the stop in progress (all null), so the latest FINISHED
    /// stop travels here; null in every other state, where the result fields themselves are the latest finished stop.
    /// </summary>
    public BrakingSnapshot? LastCompleted { get; init; }

    /// <summary>
    /// The most recent finished stop whatever the state is now: what a reader saves when <see cref="CompletedRuns"/> grew. A method,
    /// not a property, so the record's generated printing does not follow it back to the same object.
    /// </summary>
    public BrakingSnapshot GetCompletedResult() => State == BrakingState.Braking ? LastCompleted ?? Empty : this;
}

/// <summary>
/// Measures the 100-0 km/h and 60-0 mph braking distance and time from a stream of packets.
/// </summary>
/// <remarks>
/// <para><b>Two tests, one state machine.</b> The 100-0 km/h test arms at 105 km/h and starts when speed crosses BELOW
/// 100 km/h; the 60-0 mph test arms at 63 mph (101.4 km/h) and starts when speed crosses below 60 mph (96.56 km/h). A stop that
/// begins above 105 km/h crosses 100 km/h first and records BOTH (60 mph is a second line on the same deceleration); a stop from
/// 62 mph (99.8 km/h) never armed the 100 test and records only 60-0. Each arming gap (5 km/h for the 100 test, 3 mph for the
/// 60 test) is hysteresis: it proves the car really was travelling faster than the start line, so a car cruising at the line
/// cannot trigger a run by noise.</para>
///
/// <para><b>The brake.</b> Hard braking means 50% or more, and it must be reached within 0.3 s of game time after the start
/// crossing: a real pedal takes a moment to ramp, so demanding it exactly AT the crossing would reject a normal stop. A stop
/// whose brake never gets there in time (coasting, a light touch) is discarded and disarms until the car speeds up again,
/// otherwise a later tap of the pedal at 60 km/h would be reported as a 100-0 run. After the ramp the brake must stay applied
/// (a run is discarded when it stays under 30% for 0.3 s).</para>
///
/// <para><b>Aborts.</b> Throttle above 10% during the stop, reverse gear, IsRaceOn false, a game-clock rewind, speed climbing
/// back above the start line, or the brake rules above discard the run; the previous completed result stays on screen.
/// While a run is in progress <see cref="Current"/> shows THAT run's partials (no results yet, live elapsed
/// time), like <see cref="AccelerationTimer"/>, never the previous run's numbers.</para>
///
/// <para><b>Menus.</b> A packet with IsRaceOn false never flips a completed result to Idle: results stay
/// visible, and a state that is already settled is not republished, so a menu streaming packets allocates
/// nothing.</para>
///
/// <para><b>Interpolation.</b> Like <see cref="AccelerationTimer"/>, crossings are located between the two
/// straddling packets assuming linear speed change, using the game's <see cref="TelemetryPacket.TimestampMS"/>
/// (exact) rather than wall-clock time. The same fraction is applied to <see cref="TelemetryPacket.DistanceTraveled"/>
/// to get the distance at the crossing, so a 16 ms packet interval (about 0.4 m at 100 km/h) does not
/// quantize the result. "Stopped" is the interpolated instant speed crosses 1 km/h, the same standstill
/// threshold the acceleration timer uses (the last metre of a stop is a creep the game never reports as 0).</para>
///
/// <para><b>Thread-safety.</b> Identical to <see cref="AccelerationTimer"/>: one writer
/// (<see cref="OnPacket"/>), many readers; both take the same lock, and <see cref="Current"/> builds the
/// immutable snapshot on read, so a reader never sees a half-updated run. <see cref="Reset"/> clears under the
/// lock immediately instead of waiting for a packet that may never come.</para>
/// </remarks>
public sealed class BrakingTest : IPacketObserver
{
    private const double StandstillMs = SpeedCrossing.StandstillMs;
    private static readonly double Arm100Ms = SpeedCrossing.KmhToMs(105);
    private static readonly double Start100Ms = SpeedCrossing.KmhToMs(100);
    private static readonly double Arm60MphMs = SpeedCrossing.MphToMs(63);
    private static readonly double Start60MphMs = SpeedCrossing.MphToMs(60);

    /// <summary>Brake below this raw value (77 of 255, about 30%) counts as "released".</summary>
    private const byte BrakeReleased = 77;

    /// <summary>How long (game ms) the brake may stay released before the run is discarded.</summary>
    private const double ReleaseHoldMs = 300;

    /// <summary>Brake at or above this raw value (128 of 255, about 50%) counts as hard braking.</summary>
    private const byte HardBrake = 128;

    /// <summary>How long (game ms) after the start crossing the brake may take to reach <see cref="HardBrake"/> (a realistic pedal ramp).</summary>
    private const double BrakeRampMs = 300;

    /// <summary>Throttle above this raw value (25 of 255, about 10%) during the stop invalidates the run.</summary>
    private const byte ThrottleAbort = 25;

    private readonly object _gate = new();
    private BrakingSnapshot _published = BrakingSnapshot.Empty;

    // ---- Writer-only state ----
    private BrakingState _state = BrakingState.Idle;
    private TelemetryPacket? _previous;
    private BrakingSnapshot _lastCompleted = BrakingSnapshot.Empty;
    private long _completedRuns; // never reset: readers compare it with the last value they saw

    // Which tests the car has been fast enough for since it armed (latched until the run starts or is dropped).
    private bool _armed100;
    private bool _armed60;

    private double _startTimeMs; // the first line crossed: time zero of the live elapsed time
    private double _startSpeedMs; // the speed of that line: climbing back above it aborts the run
    private double _latestTimeMs;
    private double? _hundredTimeMs;
    private double? _hundredDistance;
    private double? _sixtyTimeMs;
    private double? _sixtyDistance;
    private bool _hardBrakeSeen;
    private double _hardBrakeDeadlineMs;
    private double? _brakeReleasedSinceMs;

    /// <summary>The current snapshot (built on read while braking); safe to read from any thread.</summary>
    public BrakingSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _state == BrakingState.Braking
                    ? new BrakingSnapshot(
                        BrakingState.Braking,
                        Math.Max(0, (_latestTimeMs - _startTimeMs) / 1000.0),
                        null,
                        null,
                        null,
                        null,
                        _completedRuns)
                    {
                        LastCompleted = _lastCompleted,
                    }
                    : _published;
            }
        }
    }

    /// <summary>Forgets all results and any run in progress. Safe from any thread.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _state = BrakingState.Idle;
            _lastCompleted = BrakingSnapshot.Empty with { CompletedRuns = _completedRuns };
            _previous = null; // never interpolate across the reset
            ClearRun();
            _published = _lastCompleted;
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
            Abort(packet);
            if (_state == BrakingState.Armed)
            {
                Settle(BrakingState.Idle); // not racing: nothing to arm for (a Done result stays visible)
            }

            return;
        }

        if (previous is not null && packet.TimestampMS < previous.TimestampMS)
        {
            previous = null; // the game clock went backwards: the interval is meaningless
            Abort(packet);
        }

        switch (_state)
        {
            case BrakingState.Idle:
            case BrakingState.Done:
                TryArm(packet);
                break;

            case BrakingState.Armed:
                if (packet.Gear == 0)
                {
                    Settle(BrakingState.Idle);
                }
                else
                {
                    Latch(packet);
                    if (previous is not null)
                    {
                        TryBegin(previous, packet);
                    }
                }

                break;

            case BrakingState.Braking:
                if (packet.Gear == 0 || packet.Accel > ThrottleAbort || packet.Speed >= _startSpeedMs)
                {
                    Abort(packet);
                }
                else if (!BrakeStillGood(packet))
                {
                    Abort(packet);
                }
                else if (previous is not null)
                {
                    Advance(previous, packet);
                }

                break;
        }
    }

    /// <summary>
    /// The brake rule of a run in progress: hard braking must be reached within <see cref="BrakeRampMs"/> of the start crossing,
    /// and once it was, the brake may stay released only for a short moment (<see cref="ReleaseHoldMs"/> of game time).
    /// </summary>
    private bool BrakeStillGood(TelemetryPacket packet)
    {
        if (!_hardBrakeSeen)
        {
            if (packet.TimestampMS > _hardBrakeDeadlineMs)
            {
                return false; // the ramp is over and the brake never got hard: coasting or a light touch, not a test
            }

            if (packet.Brake >= HardBrake)
            {
                _hardBrakeSeen = true;
            }

            return true;
        }

        if (packet.Brake >= BrakeReleased)
        {
            _brakeReleasedSinceMs = null;
            return true;
        }

        _brakeReleasedSinceMs ??= packet.TimestampMS;
        return packet.TimestampMS - _brakeReleasedSinceMs < ReleaseHoldMs;
    }

    private void TryArm(TelemetryPacket packet)
    {
        if (packet.Speed >= Arm60MphMs && packet.Gear != 0)
        {
            Settle(BrakingState.Armed);
            Latch(packet);
        }
    }

    /// <summary>Remembers which tests this speed proves the car was fast enough for.</summary>
    private void Latch(TelemetryPacket packet)
    {
        _armed100 |= packet.Speed >= Arm100Ms;
        _armed60 |= packet.Speed >= Arm60MphMs;
    }

    /// <summary>Starts a run when speed fell through a start line of a test that is armed (the brake is judged afterwards, over its ramp).</summary>
    private void TryBegin(TelemetryPacket previous, TelemetryPacket current)
    {
        var cross100 = _armed100 && previous.Speed >= Start100Ms && current.Speed < Start100Ms;
        var cross60 = _armed60 && previous.Speed >= Start60MphMs && current.Speed < Start60MphMs;
        if (!cross100 && !cross60)
        {
            return;
        }

        if (current.Accel > ThrottleAbort)
        {
            Abort(current); // falling through the line on the throttle is not a stop
            return;
        }

        Begin(previous, current, cross100);
        Advance(previous, current);
    }

    /// <summary>Finds the instant speed crossed the first start line between two packets; that is time zero and distance zero.</summary>
    private void Begin(TelemetryPacket previous, TelemetryPacket current, bool from100)
    {
        ClearRun();
        _state = BrakingState.Braking;

        _startSpeedMs = from100 ? Start100Ms : Start60MphMs;
        _startTimeMs = SpeedCrossing.TimeMs(previous, current, _startSpeedMs);
        var startDistance = SpeedCrossing.Distance(previous, current, _startSpeedMs);
        if (from100)
        {
            _hundredTimeMs = _startTimeMs;
            _hundredDistance = startDistance;
        }
        else
        {
            _sixtyTimeMs = _startTimeMs;
            _sixtyDistance = startDistance;
        }

        _hardBrakeSeen = current.Brake >= HardBrake;
        _hardBrakeDeadlineMs = _startTimeMs + BrakeRampMs;
        _latestTimeMs = current.TimestampMS;
    }

    /// <summary>Looks for the 60 mph line (when the run started at 100 km/h) and the stop inside one packet interval.</summary>
    private void Advance(TelemetryPacket previous, TelemetryPacket current)
    {
        if (current.TimestampMS <= previous.TimestampMS)
        {
            return;
        }

        double v0 = previous.Speed, v1 = current.Speed;

        if (_sixtyTimeMs is null && v0 >= Start60MphMs && v1 < Start60MphMs)
        {
            _sixtyTimeMs = SpeedCrossing.TimeMs(previous, current, Start60MphMs);
            _sixtyDistance = SpeedCrossing.Distance(previous, current, Start60MphMs);
        }

        if (v1 < StandstillMs)
        {
            Finish(
                SpeedCrossing.TimeMs(previous, current, StandstillMs),
                SpeedCrossing.Distance(previous, current, StandstillMs));
            return;
        }

        // No publish while braking: Current builds the running snapshot from these fields when read.
        _latestTimeMs = current.TimestampMS;
    }

    private void Finish(double stopTimeMs, double stopDistance)
    {
        _completedRuns++;
        _lastCompleted = new BrakingSnapshot(
            BrakingState.Done,
            0,
            _hundredDistance is null ? null : stopDistance - _hundredDistance,
            _hundredTimeMs is null ? null : (stopTimeMs - _hundredTimeMs) / 1000.0,
            _sixtyDistance is null ? null : stopDistance - _sixtyDistance,
            _sixtyTimeMs is null ? null : (stopTimeMs - _sixtyTimeMs) / 1000.0,
            _completedRuns);
        _state = BrakingState.Done;
        ClearRun();
        _published = _lastCompleted;
    }

    /// <summary>Drops a run in progress (the previous completed result stays) and re-evaluates arming on this packet.</summary>
    private void Abort(TelemetryPacket packet)
    {
        if (_state == BrakingState.Done)
        {
            return; // a finished result is never flipped back by a menu packet or a rewind
        }

        ClearRun();
        var armed = packet.IsRaceOn && packet.Speed >= Arm60MphMs && packet.Gear != 0;
        Settle(armed ? BrakingState.Armed : BrakingState.Idle);
        if (armed)
        {
            Latch(packet);
        }
    }

    private void Settle(BrakingState state)
    {
        if (state != BrakingState.Armed)
        {
            _armed100 = false; // an old latch must not arm a test the car is no longer fast enough for
            _armed60 = false;
        }

        if (_state == state && _published.State == state)
        {
            return; // already settled: no allocation, no republish
        }

        _state = state;
        _published = _lastCompleted with { State = state };
    }

    private void ClearRun()
    {
        _armed100 = false;
        _armed60 = false;
        _hundredTimeMs = null;
        _hundredDistance = null;
        _sixtyTimeMs = null;
        _sixtyDistance = null;
        _hardBrakeSeen = false;
        _brakeReleasedSinceMs = null;
    }
}
