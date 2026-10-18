using System.Text.Json;
using System.Text.Json.Serialization;
using ForzaTelemetry.Udp;

namespace ForzaTelemetry.App.Services;

/// <summary>Unit shown for vehicle speed.</summary>
public enum SpeedUnit
{
    Kmh = 0,
    Mph
}

/// <summary>Unit shown for tire temperature.</summary>
public enum TemperatureUnit
{
    Celsius = 0,
    Fahrenheit
}

/// <summary>Unit shown for engine power.</summary>
public enum PowerUnit
{
    Kw = 0,
    Hp
}

/// <summary>Unit shown for turbo/supercharger boost.</summary>
public enum BoostUnit
{
    Psi = 0,
    Bar
}

/// <summary>Display language: follow Windows, or force one of the languages the app ships.</summary>
public enum AppLanguage
{
    System = 0,
    English,
    Spanish
}

/// <summary>
/// Where the mini-mode window sat the last time it was closed, for ONE arrangement of monitors.
/// </summary>
/// <param name="Setup">
/// The monitor arrangement this position belongs to (see <see cref="MiniModeGeometry.SetupKey"/>). A laptop that is sometimes docked has two
/// layouts; remembering one position per layout puts the window back where it was on THIS desk instead of wherever the other layout left it.
/// </param>
/// <param name="Left">Left edge in physical pixels, in virtual-screen coordinates (negative on a monitor left of the primary one). The window's NORMAL position, never a minimized or maximized one.</param>
/// <param name="Top">Top edge in physical pixels.</param>
/// <param name="Width">Width in device-independent units (1/96 inch), shadow margin included. Not pixels: the same window is a different number of pixels on monitors of different scaling, so the size is restored in the target monitor's own scale instead of drifting each time it moves between them.</param>
/// <param name="Height">Height in device-independent units, shadow margin included.</param>
public sealed record MiniWindowPlacement(string Setup, double Left, double Top, double Width, double Height)
{
    /// <summary>True when every number is a real, positive-size rectangle and the layout is named: a hand-edited or damaged entry is dropped, not trusted.</summary>
    [JsonIgnore]
    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(Setup)
        && double.IsFinite(Left) && double.IsFinite(Top)
        && double.IsFinite(Width) && double.IsFinite(Height)
        && Width > 0 && Height > 0;
}

/// <summary>
/// Everything the user can change in Settings, as one immutable value that is saved to JSON.
/// </summary>
/// <param name="SchemaVersion">
/// Layout version of the file. The file outlives the app version that wrote it: writing the version into it is what lets a
/// future release recognise an old layout and migrate it instead of guessing. Today there is only version 1. This build
/// always WRITES its own <see cref="CurrentSchemaVersion"/> (see <see cref="Sanitized"/>): the value stored here describes the
/// layout of what this build saves, never the layout of a file it merely read.
/// </param>
/// <param name="UdpPort">The UDP port the game's Data Out sends to (1..65535).</param>
/// <param name="SpeedUnit">Unit of every speed readout.</param>
/// <param name="TemperatureUnit">Unit of every tire temperature readout.</param>
/// <param name="PowerUnit">Unit of every engine power readout (kilowatts, or mechanical horsepower).</param>
/// <param name="BoostUnit">Unit of every boost readout (psi, or bar).</param>
/// <param name="Language">Display language, applied at startup.</param>
/// <param name="StartListeningOnLaunch">Start the UDP listener as soon as the app opens (default off: the user presses Start).</param>
/// <param name="HasCompletedSetup">
/// True once the user has finished or dismissed the first-run "Connect your game" guide, so it opens by itself only on the
/// first launch (it stays reachable from Settings and from the dashboard).
/// </param>
/// <remarks>
/// <b>Why an immutable record.</b> A settings change is "replace the whole value", never "mutate a field somewhere":
/// <c>settings with { SpeedUnit = SpeedUnit.Mph }</c> builds a new copy, and record equality then answers "did anything
/// really change?" for free (so saving and notifying are skipped when the user re-selects the current option).
/// <para>
/// <b>Why a positional record with default values.</b> The defaults live in ONE place (the parameter list) and a file that
/// lacks a field simply gets that default. A record with <c>init</c> properties and property initializers looks equivalent but
/// is not: System.Text.Json's source generator builds init-only properties without running their initializers, so a missing
/// JSON field would silently become 0/false. Constructor parameters with default values are honored.
/// </para>
/// <para>
/// <b>Unknown fields survive a round trip.</b> A newer version of the app may add settings this build has never heard of.
/// If this build loaded such a file and saved it back, plain deserialization would silently drop them and the newer version
/// would lose the user's choices after a downgrade. <see cref="ExtensionData"/> keeps every unrecognized property exactly as
/// it was read, and <see cref="JsonSettingsStore"/> writes them back next to the known ones. (It is deliberately not
/// System.Text.Json's <c>[JsonExtensionData]</c> attribute: the source generator treats init-only properties as constructor
/// parameters and refuses to bind an extension-data slot to one, and the record is meant to stay immutable.) The property
/// takes part in record equality by reference, which is the right answer here: a copy made with <c>with</c> shares the same
/// dictionary, so equality still means "did a KNOWN setting change?".
/// </para>
/// </remarks>
public sealed record AppSettings(
    int SchemaVersion = AppSettings.CurrentSchemaVersion,
    int UdpPort = UdpTelemetryListener.DefaultPort,
    SpeedUnit SpeedUnit = SpeedUnit.Kmh,
    TemperatureUnit TemperatureUnit = TemperatureUnit.Celsius,
    AppLanguage Language = AppLanguage.System,
    bool StartListeningOnLaunch = false,
    bool HasCompletedSetup = false,
    bool HideMainWindowInMiniMode = true,
    bool MiniAlwaysOnTop = true,
    double MiniOpacity = AppSettings.MaxMiniOpacity,
    IReadOnlyList<MiniWindowPlacement>? MiniPlacements = null,
    PowerUnit PowerUnit = PowerUnit.Kw,
    BoostUnit BoostUnit = BoostUnit.Psi)
{
    /// <summary>Properties found in the file that this build does not know (written by a newer version); kept so a save does not drop them.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, JsonElement>? ExtensionData { get; init; }

    /// <summary>The layout version this build reads and writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Lowest valid UDP port.</summary>
    public const int MinPort = 1;

    /// <summary>Highest valid UDP port.</summary>
    public const int MaxPort = 65535;

    /// <summary>Lowest mini-window opacity: still readable over a bright track, yet clearly see-through.</summary>
    public const double MinMiniOpacity = 0.6;

    /// <summary>Fully opaque.</summary>
    public const double MaxMiniOpacity = 1.0;

    /// <summary>How many monitor arrangements keep a remembered mini-window position (the oldest is forgotten first), so the file cannot grow forever.</summary>
    public const int MaxMiniPlacements = 8;

    /// <summary>The settings a fresh install starts with.</summary>
    public static AppSettings Default { get; } = new();

    /// <summary>True when <paramref name="port"/> can be bound: a whole number in 1..65535.</summary>
    public static bool IsValidPort(int port) => port is >= MinPort and <= MaxPort;

    /// <summary>
    /// A copy that is safe to use: any value that is out of range (a hand-edited file, a number cast to an enum) is
    /// replaced by its default, field by field, so one bad value never discards the user's other choices. The schema
    /// version is always this build's own: whatever version the file claimed, what this build writes is its layout.
    /// </summary>
    public AppSettings Sanitized() => this with
    {
        SchemaVersion = CurrentSchemaVersion,
        UdpPort = IsValidPort(UdpPort) ? UdpPort : Default.UdpPort,
        SpeedUnit = Enum.IsDefined(SpeedUnit) ? SpeedUnit : Default.SpeedUnit,
        TemperatureUnit = Enum.IsDefined(TemperatureUnit) ? TemperatureUnit : Default.TemperatureUnit,
        Language = Enum.IsDefined(Language) ? Language : Default.Language,
        PowerUnit = Enum.IsDefined(PowerUnit) ? PowerUnit : Default.PowerUnit,
        BoostUnit = Enum.IsDefined(BoostUnit) ? BoostUnit : Default.BoostUnit,
        MiniOpacity = ClampMiniOpacity(MiniOpacity),
        MiniPlacements = SanitizedPlacements(MiniPlacements)
    };

    /// <summary>
    /// The ONE opacity policy, used by the settings reader, <see cref="Sanitized"/>, the mini-mode view model and its slider: clamp into
    /// <see cref="MinMiniOpacity"/>..<see cref="MaxMiniOpacity"/>; NaN (a damaged value) means fully opaque. Clamping rather than rejecting
    /// keeps a hand-edited 0.3 as the nearest legal value (60%) instead of silently jumping to 100%.
    /// </summary>
    public static double ClampMiniOpacity(double opacity) =>
        double.IsNaN(opacity) ? MaxMiniOpacity : Math.Clamp(opacity, MinMiniOpacity, MaxMiniOpacity);

    // Keeps the usable entries, one per monitor arrangement (the LAST one wins: it is the most recent), and only the newest few.
    // Returns the same list when nothing needed fixing, so a sanitized copy of clean settings still compares equal to the original.
    private static IReadOnlyList<MiniWindowPlacement>? SanitizedPlacements(IReadOnlyList<MiniWindowPlacement>? placements)
    {
        if (placements is null)
        {
            return null;
        }

        var kept = placements
            .Where(p => p is { IsUsable: true })
            .Reverse()
            .DistinctBy(p => p.Setup)
            .Take(MaxMiniPlacements)
            .Reverse()
            .ToList();

        return kept.SequenceEqual(placements) ? placements : kept;
    }
}
