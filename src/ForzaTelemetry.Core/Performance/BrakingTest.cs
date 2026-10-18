namespace ForzaTelemetry.Core.Performance;

/// <summary>Where the <see cref="BrakingTest"/> state machine currently is.</summary>
public enum BrakingState
{
    /// <summary>Not ready: below the arming speed, or a run was aborted. Speeding up past the arming speed re-arms.</summary>
    Idle,

    /// <summary>Fast enough (and racing) that braking hard below 100 km/h will start a measurement.</summary>
    Armed,

    /// <summary>A stop is being measured.</summary>
    Braking,

    /// <summary>The car stopped; results are final until the next run starts.</summary>
    Done,
}

/// <summary>An immutable picture of the braking test. Null results mean "not measured (yet)".</summary>
/// <param name="State">Current state of the machine.</param>
/// <param name="ElapsedSeconds">Seconds since the 100 km/h crossing while <see cref="BrakingState.Braking"/>; 0 otherwise.</param>
/// <param name="HundredToZeroMeters">Distance covered braking from 100 km/h to a stop.</param>
/// <param name="HundredToZeroSeconds">Time taken braking from 100 km/h to a stop.</param>
/// <param name="SixtyToZeroMphMeters">Distance covered braking from 60 mph (96.56 km/h) to a stop.</param>
/// <param name="SixtyToZeroMphSeconds">Time taken braking from 60 mph to a stop.</param>
public sealed record BrakingSnapshot(
    BrakingState State,
    double ElapsedSeconds,
    double? HundredToZeroMeters,
    double? HundredToZeroSeconds,
    double? SixtyToZeroMphMeters,
    double? SixtyToZeroMphSeconds)
{
    /// <summary>The initial snapshot: idle, nothing measured.</summary>
    public static BrakingSnapshot Empty { get; } = new(BrakingState.Idle, 0, null, null, null, null);
}

/// <summary>
/// Measures the 100-0 km/h and 60-0 mph braking distance and time from a stream of packets.
/// </summary>
/// <remarks>
/// <para><b>State machine.</b> Idle -> Armed (race on, forward gear, speed at least 105 km/h) -> Braking (speed
/// crosses BELOW 100 km/h with the brake at 50% or more) -> Done (speed under 1 km/h). The 5 km/h gap between
/// arming (105) and starting (100) is hysteresis: it proves the car really was travelling faster than the
/// start line, so a car cruising at exactly 100 km/h cannot trigger a run by noise. Dropping under 100 km/h
/// WITHOUT the brake (coasting) disarms until the car speeds up again, otherwise a later tap of the pedal at
/// 60 km/h would be reported as a 100-0 run.</para>
///
/// <para><b>Aborts.</b> Throttle above 10% during the stop, reverse gear, IsRaceOn false, a game-clock rewind or
/// speed climbing back above 100 km/h discard the run; the previous completed result stays on screen.</para>
///
/// <para><b>Both variants from one stop.</b> 60 mph (96.56 km/h) is just a second line on the same deceleration,
/// crossed a moment after 100 km/h. The run records the time and distance at each crossing and subtracts them
/// from the stop, so the two results cost nothing extra and are always consistent with each other.</para>
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
    private const double StandstillMs = 1.0 / 3.6;
    private const double ArmSpeedMs = 105.0 / 3.6;
    private const double StartSpeedMs = 100.0 / 3.6;
    private const double SixtyMphMs = 60.0 * AccelerationTimer.KmhPerMph / 3.6;

    /// <summary>Brake at or above this raw value (128 of 255, about 50%) counts as hard braking.</summary>
    private const byte HardBrake = 128;

    /// <summary>Throttle above this raw value (25 of 255, about 10%) during the stop invalidates the run.</summary>
    private const byte ThrottleAbort = 25;

    private readonly object _gate = new();
    private BrakingSnapshot _published = BrakingSnapshot.Empty;

    // ---- Writer-only state ----
    private BrakingState _state = BrakingState.Idle;
    private TelemetryPacket? _previous;
    private BrakingSnapshot _lastCompleted = BrakingSnapshot.Empty;
    private double _startTimeMs;
    private double _startDistance;
    private double _latestTimeMs;
    private double? _sixtyTimeMs;
    private double? _sixtyDistance;

    /// <summary>The current snapshot (built on read while braking); safe to read from any thread.</summary>
    public BrakingSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _state == BrakingState.Braking
                    ? _lastCompleted with
                    {
                        State = BrakingState.Braking,
                        ElapsedSeconds = Math.Max(0, (_latestTimeMs - _startTimeMs) / 1000.0),
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
            _lastCompleted = BrakingSnapshot.Empty;
            _previous = null; // never interpolate across the reset
            ClearRun();
            _published = BrakingSnapshot.Empty;
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
                else if (previous is not null && previous.Speed >= StartSpeedMs && packet.Speed < StartSpeedMs)
                {
                    if (packet.Brake >= HardBrake)
                    {
                        Begin(previous, packet);
                        Advance(previous, packet);
                    }
                    else
                    {
                        Settle(BrakingState.Idle); // coasted through 100: wait to be re-armed by speeding up
                    }
                }

                break;

            case BrakingState.Braking:
                if (packet.Gear == 0 || packet.Accel > ThrottleAbort || packet.Speed >= StartSpeedMs)
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

    private void TryArm(TelemetryPacket packet)
    {
        if (packet.Speed >= ArmSpeedMs && packet.Gear != 0)
        {
            Settle(BrakingState.Armed);
        }
    }

    /// <summary>Finds the instant speed crossed 100 km/h between two packets; that is time zero and distance zero.</summary>
    private void Begin(TelemetryPacket previous, TelemetryPacket current)
    {
        ClearRun();
        _state = BrakingState.Braking;

        var fraction = Fraction(previous.Speed, current.Speed, StartSpeedMs);
        _startTimeMs = Lerp(previous.TimestampMS, current.TimestampMS, fraction);
        _startDistance = Lerp(previous.DistanceTraveled, current.DistanceTraveled, fraction);
        _latestTimeMs = current.TimestampMS;
    }

    /// <summary>Looks for the 60 mph line and the stop inside one packet interval.</summary>
    private void Advance(TelemetryPacket previous, TelemetryPacket current)
    {
        if (current.TimestampMS <= previous.TimestampMS)
        {
            return;
        }

        double v0 = previous.Speed, v1 = current.Speed;

        if (_sixtyTimeMs is null && v0 >= SixtyMphMs && v1 < SixtyMphMs)
        {
            var f = Fraction(v0, v1, SixtyMphMs);
            _sixtyTimeMs = Lerp(previous.TimestampMS, current.TimestampMS, f);
            _sixtyDistance = Lerp(previous.DistanceTraveled, current.DistanceTraveled, f);
        }

        if (v1 < StandstillMs)
        {
            var f = Fraction(v0, v1, StandstillMs);
            var stopTimeMs = Lerp(previous.TimestampMS, current.TimestampMS, f);
            var stopDistance = Lerp(previous.DistanceTraveled, current.DistanceTraveled, f);
            Finish(stopTimeMs, stopDistance);
            return;
        }

        // No publish while braking: Current builds the running snapshot from these fields when read.
        _latestTimeMs = current.TimestampMS;
    }

    private void Finish(double stopTimeMs, double stopDistance)
    {
        _lastCompleted = new BrakingSnapshot(
            BrakingState.Done,
            0,
            stopDistance - _startDistance,
            (stopTimeMs - _startTimeMs) / 1000.0,
            _sixtyDistance is null ? null : stopDistance - _sixtyDistance,
            _sixtyTimeMs is null ? null : (stopTimeMs - _sixtyTimeMs) / 1000.0);
        _state = BrakingState.Done;
        ClearRun();
        _published = _lastCompleted;
    }

    /// <summary>Drops a run in progress (the previous completed result stays) and re-evaluates arming on this packet.</summary>
    private void Abort(TelemetryPacket packet)
    {
        ClearRun();
        var armed = packet.IsRaceOn && packet.Speed >= ArmSpeedMs && packet.Gear != 0;
        Settle(armed ? BrakingState.Armed : BrakingState.Idle);
    }

    private void Settle(BrakingState state)
    {
        _state = state;
        _published = _lastCompleted with { State = state };
    }

    private void ClearRun()
    {
        _sixtyTimeMs = null;
        _sixtyDistance = null;
    }

    /// <summary>Fraction of the interval (0..1) at which a falling speed crossed <paramref name="target"/>.</summary>
    private static double Fraction(double v0, double v1, double target) =>
        v0 > v1 ? Math.Clamp((v0 - target) / (v0 - v1), 0, 1) : 1;

    private static double Lerp(double a, double b, double fraction) => a + (fraction * (b - a));
}
