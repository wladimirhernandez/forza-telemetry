namespace ForzaTelemetry.App;

/// <summary>
/// Outline icons (24 x 24 box, stroke only) for the placeholder screens, as WPF path mini-language strings.
/// </summary>
/// <remarks>
/// They are strings and not <c>Geometry</c> objects so the view models stay free of WPF types: a <c>Path.Data</c> binding
/// to a string works because WPF runs the string through its <c>GeometryConverter</c> (the same conversion that lets you
/// write <c>Data="M 0,0 L 10,10"</c> in XAML).
/// </remarks>
public static class PageIcons
{
    /// <summary>Speedometer: an open arc with a needle.</summary>
    public const string Performance = "M 4,18 A 9,9 0 1 1 20,18 M 12,13 L 16.5,8.5 M 12,3 V 5 M 3.5,9 L 5.2,10 M 20.5,9 L 18.8,10";

    /// <summary>Checkered-flag style pennant on a pole.</summary>
    public const string Laps = "M 5,21 V 3.5 M 5,4.5 H 19 L 16,8.5 L 19,12.5 H 5";

    /// <summary>A film frame with a play triangle: recorded sessions you can replay.</summary>
    public const string Sessions = "M 3.5,5 H 20.5 V 19 H 3.5 Z M 10,9 L 15,12 L 10,15 Z";

    /// <summary>A line chart over its axes.</summary>
    public const string Analysis = "M 3,20.5 H 21 M 3.5,3.5 V 20.5 M 6.5,16.5 L 10.5,10.5 L 13.5,14 L 19.5,6";

    /// <summary>A folded map.</summary>
    public const string Map = "M 9,4 L 3,6 V 20 L 9,18 L 15,20 L 21,18 V 4 L 15,6 Z M 9,4 V 18 M 15,6 V 20";

    /// <summary>Three sliders: the classic tuning symbol.</summary>
    public const string Tuning = "M 4,7 H 20 M 4,12 H 20 M 4,17 H 20 M 9,4 V 10 M 15,9 V 15 M 8,14 V 20";

    /// <summary>Two sliders: preferences.</summary>
    public const string Settings = "M 4,8 H 20 M 4,16 H 20 M 8,5 V 11 M 16,13 V 19";
}
