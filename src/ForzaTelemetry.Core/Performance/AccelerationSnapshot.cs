namespace ForzaTelemetry.Core.Performance;

/// <summary>Where the <see cref="AccelerationTimer"/> state machine currently is.</summary>
public enum AccelerationState
{
    /// <summary>Not ready: the car is moving, in a menu, or a run was aborted away from the line.</summary>
    Idle,

    /// <summary>Stationary with the race on: the next movement starts a run.</summary>
    Armed,

    /// <summary>A run is in progress.</summary>
    Running,

    /// <summary>The run finished; results are final until the next run starts.</summary>
    Done,
}

/// <summary>
/// An immutable picture of the acceleration timer. Null times mean "not reached (yet)".
/// </summary>
/// <param name="State">Current state of the machine.</param>
/// <param name="ElapsedSeconds">Seconds since the run started while <see cref="AccelerationState.Running"/>; 0 otherwise.</param>
/// <param name="ZeroTo100KmhSeconds">0-100 km/h time.</param>
/// <param name="ZeroTo200KmhSeconds">0-200 km/h time.</param>
/// <param name="QuarterMileSeconds">Time to cover 402.336 m.</param>
/// <param name="QuarterMileTrapSpeedKmh">Speed at the quarter-mile line.</param>
public sealed record AccelerationSnapshot(
    AccelerationState State,
    double ElapsedSeconds,
    double? ZeroTo100KmhSeconds,
    double? ZeroTo200KmhSeconds,
    double? QuarterMileSeconds,
    double? QuarterMileTrapSpeedKmh)
{
    /// <summary>The initial snapshot: idle, nothing measured.</summary>
    public static AccelerationSnapshot Empty { get; } = new(AccelerationState.Idle, 0, null, null, null, null);
}
