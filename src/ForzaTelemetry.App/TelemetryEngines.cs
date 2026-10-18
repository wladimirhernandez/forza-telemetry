using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using ForzaTelemetry.Core.Laps;
using ForzaTelemetry.Core.Performance;

namespace ForzaTelemetry.App;

/// <summary>
/// Every engine that must see EVERY packet, created together, plus the one list the telemetry service is given.
/// </summary>
/// <remarks>
/// <b>Why a class for this.</b> Two things must hold and neither is visible from the screens: every engine has to be
/// registered with the service (forgetting one gives a page that silently never updates), and the order matters (the
/// <see cref="CarChangeDetector"/> must run before the engines it resets, see its remarks). Building them here, once, and
/// exposing a single <see cref="Observers"/> list makes both facts one tested place instead of a convention in the
/// composition root.
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

        // CarChanges first: it freezes and resets the engines that follow it (see its remarks).
        Observers = [CarChanges, Acceleration, TopSpeed, Laps];
    }

    /// <summary>The 0-100 / 0-200 / quarter-mile timer.</summary>
    public AccelerationTimer Acceleration { get; }

    /// <summary>The session top speed.</summary>
    public TopSpeedTracker TopSpeed { get; }

    /// <summary>The lap list and live delta.</summary>
    public LapTracker Laps { get; }

    /// <summary>Notices a change of car on every packet.</summary>
    public CarChangeDetector CarChanges { get; }

    /// <summary>What the telemetry service calls for every packet, in this order.</summary>
    public IReadOnlyList<IPacketObserver> Observers { get; }
}
