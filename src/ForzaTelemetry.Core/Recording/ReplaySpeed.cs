namespace ForzaTelemetry.Core.Recording;

/// <summary>Named playback-speed factors (multiples of real time) for <see cref="ReplayDatagramSource.Speed"/>.</summary>
public static class ReplaySpeed
{
    /// <summary>Half speed: gaps between records last twice as long.</summary>
    public const double Half = 0.5;

    /// <summary>Real time: gaps are honored exactly.</summary>
    public const double Normal = 1;

    /// <summary>Twice real time.</summary>
    public const double Double = 2;

    /// <summary>Four times real time.</summary>
    public const double Quadruple = 4;

    /// <summary>
    /// No delay at all: records are delivered as fast as the consumer pulls them. Used by tests and to
    /// "fast-forward" a whole session instantly.
    /// </summary>
    public const double Max = double.PositiveInfinity;
}
