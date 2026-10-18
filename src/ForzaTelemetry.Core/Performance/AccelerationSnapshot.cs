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
/// <param name="HundredTo200KmhSeconds">100-200 km/h "rolling" time: 100 km/h crossing to 200 km/h crossing of the same run.</param>
/// <param name="ZeroTo60MphSeconds">0-60 mph time (96.56 km/h).</param>
/// <param name="SixtyTo130MphSeconds">60-130 mph time, from the same run.</param>
/// <param name="CompletedRuns">How many runs the timer has KEPT since it was created; it only ever grows (not even <see cref="AccelerationTimer.Reset"/> lowers it). A reader that remembers the last value it saw can tell "a run was kept since I looked" without comparing result values, which repeat (two identical runs) and cannot tell a dropped run from a kept one.</param>
/// <remarks>
/// The last three parameters were added after the first release and DEFAULT to null, so positional and named
/// construction and <c>with</c> expressions within this solution keep compiling and behaving the same. It is NOT
/// fully compatible: <c>Deconstruct</c> gained three outputs and the constructor's binary signature changed,
/// which is acceptable because the whole app is one set of projects built together.
/// </remarks>
public sealed record AccelerationSnapshot(
    AccelerationState State,
    double ElapsedSeconds,
    double? ZeroTo100KmhSeconds,
    double? ZeroTo200KmhSeconds,
    double? QuarterMileSeconds,
    double? QuarterMileTrapSpeedKmh,
    double? HundredTo200KmhSeconds = null,
    double? ZeroTo60MphSeconds = null,
    double? SixtyTo130MphSeconds = null,
    long CompletedRuns = 0)
{
    /// <summary>The initial snapshot: idle, nothing measured.</summary>
    public static AccelerationSnapshot Empty { get; } = new(AccelerationState.Idle, 0, null, null, null, null);
}
