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

/// <summary>Display language: follow Windows, or force one of the languages the app ships.</summary>
public enum AppLanguage
{
    System = 0,
    English,
    Spanish
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
    bool HasCompletedSetup = false)
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
        Language = Enum.IsDefined(Language) ? Language : Default.Language
    };
}
