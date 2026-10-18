using ForzaTelemetry.Core;
using ForzaTelemetry.Core.Performance;

namespace ForzaTelemetry.App.Services;

/// <summary>
/// One change of the car being driven, as seen by the receive loop: who was driven before, how fast they went, and who is
/// driven now.
/// </summary>
/// <param name="PreviousCar">The car driven before, or null for the first real car of a session.</param>
/// <param name="PreviousClass">The game's class number of the previous car (meaningless when <paramref name="PreviousCar"/> is null).</param>
/// <param name="PreviousDrivetrain">The game's drivetrain number of the previous car.</param>
/// <param name="PreviousTopSpeedKmh">The previous car's top speed, frozen at the moment of the change (0 when none or unknown).</param>
/// <param name="Car">The car driven now.</param>
/// <param name="CarClass">The game's class number of the new car.</param>
/// <param name="Drivetrain">The game's drivetrain number of the new car.</param>
public sealed record CarChange(
    CarKey? PreviousCar,
    int PreviousClass,
    int PreviousDrivetrain,
    double PreviousTopSpeedKmh,
    CarKey Car,
    int CarClass,
    int Drivetrain);

/// <summary>
/// Notices a change of car on EVERY packet (it is a packet observer) and queues what the screens need to react to it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why per packet and not at the 20 Hz UI tick.</b> The engines that measure a car (top speed, the 0-100 timer) see every
/// packet, so a change of car has to be noticed at the same granularity. A view model that only looked at the latest packet
/// each tick could let several packets of the NEW car flow into the engines before it noticed, so the old car's best would be
/// polluted by the new car's speeds, and a change that came and went between two ticks would never be seen at all.
/// </para>
/// <para>
/// <b>Why it resets the engines itself.</b> This observer must be registered BEFORE the engines it owns (see
/// <see cref="TelemetryEngines"/>): the service calls observers in order, so at the moment a packet of a new car arrives the
/// engines still hold only the old car's data. The detector reads the old top speed (freezing it into the
/// <see cref="CarChange"/>) and resets the engines, and only then do the engines see the new car's first packet. Doing that
/// from the UI thread instead would reopen the very window this class exists to close.
/// </para>
/// <para>
/// <b>Hand-off to the UI.</b> Changes are rare (the player picks another car), so a small locked queue is plenty: the
/// writer is the receive loop, the reader is the UI tick (<see cref="TryTake"/>), and nothing mutable is shared. Only real cars
/// count: a packet without a valid performance index (loading, menus) neither starts nor ends anything, and packets with
/// <c>IsRaceOn</c> false are ignored like everywhere else.
/// </para>
/// </remarks>
public sealed class CarChangeDetector : IPacketObserver
{
    private readonly TopSpeedTracker? _topSpeed;
    private readonly AccelerationTimer? _acceleration;
    private readonly BrakingTest? _braking;
    private readonly Queue<CarChange> _changes = new();
    private readonly object _gate = new();

    private CarKey? _car;
    private int _carClass;
    private int _drivetrain;
    private int _generation;
    private volatile bool _engineResetRequested; // written under _gate; volatile so the per-packet check needs no lock

    /// <param name="topSpeed">The top-speed engine to freeze and reset on a change; optional (tests, or nothing to own).</param>
    /// <param name="acceleration">The 0-100 engine to reset on a change; optional.</param>
    /// <param name="braking">The braking test to reset on a change (its stop belongs to one car too); optional.</param>
    public CarChangeDetector(TopSpeedTracker? topSpeed = null, AccelerationTimer? acceleration = null, BrakingTest? braking = null)
    {
        _topSpeed = topSpeed;
        _acceleration = acceleration;
        _braking = braking;
    }

    /// <summary>
    /// Identifies WHICH car the engines currently hold data of: it grows by one on every change of car and on every
    /// <see cref="Reset"/>, both under the same lock that resets the engines.
    /// </summary>
    /// <remarks>
    /// A reader on another thread (the UI tick) cannot read "the car" and "the engines' numbers" in one atomic step, because
    /// the receive loop may switch cars between the two reads, so it would credit the new car's numbers to the old one.
    /// Instead it drains the queued changes, reads this number, checks <see cref="HasPendingChanges"/>, reads the engines and
    /// reads this number again: when both reads agree and nothing was pending, no change happened in between and the values
    /// belong to the car it knows. See <c>PerformanceViewModel.Refresh</c>.
    /// </remarks>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>
    /// True while a change of car has been noticed but not yet taken with <see cref="TryTake"/>. The change is queued under the
    /// same lock that publishes the new <see cref="Generation"/>, so "generation moved" always implies "pending or already taken".
    /// </summary>
    public bool HasPendingChanges
    {
        get
        {
            lock (_gate)
            {
                return _changes.Count > 0;
            }
        }
    }

    /// <summary>
    /// True from <see cref="RequestEngineReset"/> until the receive loop has carried it out. While it is true the engines still
    /// hold the numbers that are about to be forgotten, so a reader must treat them as already empty.
    /// </summary>
    public bool HasPendingEngineReset => _engineResetRequested;

    /// <summary>
    /// Asks for the top-speed and 0-100 engines to be reset, WITHOUT touching them: the reset is carried out by the receive loop
    /// itself on its next packet (<see cref="OnPacket"/>), the only thread that is allowed to write them. Resetting them here, from
    /// the UI thread, would race the loop's own writes (a lost sample, or a half-reset timer that goes on timing). Until the
    /// request is carried out <see cref="HasPendingEngineReset"/> is true, and a reader shows the engines as empty.
    /// </summary>
    /// <remarks>
    /// Needs a packet to be carried out, which is fine: with no packets there is no loop writing anything, and the next session
    /// start resets every engine before its loop exists (see <see cref="TelemetryEngines.BeginSession"/>).
    /// </remarks>
    public void RequestEngineReset()
    {
        lock (_gate)
        {
            _engineResetRequested = true;
        }
    }

    /// <inheritdoc />
    public void OnPacket(TelemetryPacket packet)
    {
        // Any packet carries a pending reset out, menus and pauses included: the user asked for empty engines, whatever the
        // game is doing. Checked before the race-on filter below for that reason.
        if (_engineResetRequested)
        {
            ApplyEngineReset();
        }

        if (!packet.IsRaceOn)
        {
            return;
        }

        var key = new CarKey(packet.CarOrdinal, packet.CarPerformanceIndex);
        if (!key.IsValid)
        {
            return; // not a car (loading, a menu): keep following the one we had
        }

        lock (_gate)
        {
            if (key == _car)
            {
                // The same build. Class and drivetrain follow its latest packets, so the NEXT change reports them correctly.
                _carClass = packet.CarClass;
                _drivetrain = packet.DrivetrainType;
                return;
            }

            var previous = _car;
            var previousClass = _carClass;
            var previousDrivetrain = _drivetrain;
            double previousTopSpeed = 0;

            if (previous is not null)
            {
                // Freeze the old car's best BEFORE the reset, while the engines still hold only its data (see the remarks).
                previousTopSpeed = _topSpeed?.TopSpeedKmh ?? 0;
                _topSpeed?.Reset();
                _acceleration?.Reset();
                _braking?.Reset();
            }

            _car = key;
            _carClass = packet.CarClass;
            _drivetrain = packet.DrivetrainType;

            // Published AFTER the engines were reset above and BEFORE the lock is released, so a reader that sees the new
            // generation also sees the reset engines (never the other way round).
            Interlocked.Increment(ref _generation);
            _changes.Enqueue(new CarChange(
                previous, previousClass, previousDrivetrain, previousTopSpeed, key, packet.CarClass, packet.DrivetrainType));
        }
    }

    private void ApplyEngineReset()
    {
        lock (_gate)
        {
            if (!_engineResetRequested)
            {
                return;
            }

            _topSpeed?.Reset();
            _acceleration?.Reset();
            _braking?.Reset();

            // Same publication order as a change of car: generation first, flag last. A reader that sees the flag cleared
            // therefore also sees the reset engines, and a reader that straddled the reset sees the generation move.
            Interlocked.Increment(ref _generation);
            _engineResetRequested = false;
        }
    }

    /// <summary>Takes the oldest change nobody has handled yet; false when there is none. Called by the UI tick.</summary>
    public bool TryTake(out CarChange? change)
    {
        lock (_gate)
        {
            return _changes.TryDequeue(out change);
        }
    }

    /// <summary>
    /// A new listening session starts: forgets the current car and drops every unhandled change, so the first real car of the
    /// new session is reported as a first car (<see cref="CarChange.PreviousCar"/> null) even if it is the same one as before.
    /// Safe from any thread (the receive loop and this share one lock).
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _car = null;
            _carClass = 0;
            _drivetrain = 0;
            _changes.Clear();
            _engineResetRequested = false; // the session reset that calls this one empties the engines itself
            Interlocked.Increment(ref _generation);
        }
    }
}
