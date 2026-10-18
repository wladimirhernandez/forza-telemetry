using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using ForzaTelemetry.Core.Insights;
using ForzaTelemetry.Core.Laps;
using ForzaTelemetry.Core.Performance;

namespace ForzaTelemetry.App;

/// <summary>
/// Every engine that must see EVERY packet, created together, plus the one list the telemetry service is given.
/// </summary>
/// <remarks>
/// <b>Session reset semantics.</b> A successful fresh Start gives the history, the path and the bottoming tallies a clean slate
/// (the screens that own them do it, see their <c>BeginSession</c>); the dyno curves are kept (see <see cref="Dyno"/>).
/// <para>
/// <b>Why a class for this.</b> Two things must hold and neither is visible from the screens: every engine has to be
/// registered with the service (forgetting one gives a page that silently never updates), and the order matters (the
/// <see cref="CarChangeDetector"/> must run before the engines it resets, see its remarks). Building them here, once, and
/// exposing a single <see cref="Observers"/> list makes both facts one tested place instead of a convention in the
/// composition root.
/// </para>
/// </remarks>
public sealed class TelemetryEngines
{
    /// <summary>Builds every engine and the ordered observer list.</summary>
    public TelemetryEngines()
    {
        Acceleration = new AccelerationTimer();
        TopSpeed = new TopSpeedTracker();
        Laps = new LapTracker();
        CarChanges = new CarChangeDetector(TopSpeed, Acceleration);

        // The engines behind the Analysis, Map and Tuning pages. They are plain observers (nothing resets anything else),
        // but they live here for the same reason as the rest: one place that creates them and one list that registers them.
        History = new TelemetryHistory();
        Path = new TrackPath();
        Dyno = new DynoCapture();
        Bottoming = new SuspensionBottomingCounter();

        // CarChanges first: it freezes and resets the engines that follow it (see its remarks).
        Observers = [CarChanges, Acceleration, TopSpeed, Laps, History, Path, Dyno, Bottoming];
    }

    /// <summary>The 0-100 / 0-200 / quarter-mile timer.</summary>
    public AccelerationTimer Acceleration { get; }

    /// <summary>The session top speed.</summary>
    public TopSpeedTracker TopSpeed { get; }

    /// <summary>The lap list and live delta.</summary>
    public LapTracker Laps { get; }

    /// <summary>Notices a change of car on every packet.</summary>
    public CarChangeDetector CarChanges { get; }

    /// <summary>The last minute of samples (about 3600 at 60 Hz): the Analysis traces, and the car position for the Map.</summary>
    public TelemetryHistory History { get; }

    /// <summary>The driven path of the session, colored by speed: the Map page.</summary>
    public TrackPath Path { get; }

    /// <summary>
    /// Power and torque curves captured from full-throttle pulls, per car build. The only engine that is NEVER reset by a new
    /// session: a curve is a property of the car, not of the listening session, so it survives Stop/Start (in memory; it is
    /// not saved to disk) and a returning car finds its curve again.
    /// </summary>
    public DynoCapture Dyno { get; }

    /// <summary>Per-corner suspension bottoming tallies of the session.</summary>
    public SuspensionBottomingCounter Bottoming { get; }

    /// <summary>What the telemetry service calls for every packet, in this order.</summary>
    public IReadOnlyList<IPacketObserver> Observers { get; }
}
