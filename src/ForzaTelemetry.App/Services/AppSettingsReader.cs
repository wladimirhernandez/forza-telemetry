using System.Text.Json;

namespace ForzaTelemetry.App.Services;

/// <summary>What <see cref="AppSettingsReader.Read"/> found in a settings file.</summary>
/// <param name="Settings">The settings, with every unreadable field replaced by its default.</param>
/// <param name="FileSchemaVersion">The layout version the FILE claimed (the settings themselves always carry this build's version).</param>
/// <param name="RejectedFields">Names of the fields whose value was present but unusable (wrong type, unknown enum name, out of range).</param>
internal sealed record SettingsReadResult(AppSettings Settings, int FileSchemaVersion, IReadOnlyList<string> RejectedFields);

/// <summary>
/// Reads a settings file one field at a time, so a single bad value costs only that value.
/// </summary>
/// <remarks>
/// <b>Why not just <c>JsonSerializer.Deserialize&lt;AppSettings&gt;</c>.</b> Plain deserialization is all-or-nothing: one
/// field of the wrong type (<c>"udpPort": "five thousand"</c>) or an enum name this build does not know throws, and the
/// caller can only fall back to the defaults for EVERYTHING, wiping the user's good choices (units, language, auto-start)
/// because of one typo or one value written by a different version. Here the file is parsed into a document and each known
/// property is read on its own, with its own fallback.
/// <para>
/// What is lenient: property names match case-insensitively; enums are accepted by name in any casing (the file now
/// writes <c>"mph"</c>, older builds wrote <c>"Mph"</c>) or as a defined number; properties this build does not know are
/// kept in <see cref="AppSettings.ExtensionData"/>. What is still an error (a <see cref="JsonException"/>, so the file is
/// treated as corrupt): text that is not JSON at all, or JSON whose root is not an object, since there is nothing to
/// salvage field by field.
/// </para>
/// </remarks>
internal static class AppSettingsReader
{
    public static SettingsReadResult Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The settings file does not contain a settings object.");
        }

        var settings = AppSettings.Default;
        var fileVersion = AppSettings.CurrentSchemaVersion;
        var rejected = new List<string>();
        Dictionary<string, JsonElement>? unknown = null;

        foreach (var property in root.EnumerateObject())
        {
            var name = property.Name;
            var value = property.Value;
            var accepted = true;

            // One case per known field. `accepted` stays true unless the value is present but unusable; the cheap
            // ToLowerInvariant makes the property-name match case-insensitive without a chain of string comparisons.
            switch (name.ToLowerInvariant())
            {
                case "schemaversion":
                    accepted = TryReadInt(value, out var version) && version > 0;
                    fileVersion = accepted ? version : fileVersion;
                    break;

                case "udpport":
                    accepted = TryReadInt(value, out var port) && AppSettings.IsValidPort(port);
                    settings = accepted ? settings with { UdpPort = port } : settings;
                    break;

                case "speedunit":
                    accepted = TryReadEnum(value, out SpeedUnit speedUnit);
                    settings = accepted ? settings with { SpeedUnit = speedUnit } : settings;
                    break;

                case "temperatureunit":
                    accepted = TryReadEnum(value, out TemperatureUnit temperatureUnit);
                    settings = accepted ? settings with { TemperatureUnit = temperatureUnit } : settings;
                    break;

                case "language":
                    accepted = TryReadEnum(value, out AppLanguage language);
                    settings = accepted ? settings with { Language = language } : settings;
                    break;

                case "startlisteningonlaunch":
                    accepted = value.ValueKind is JsonValueKind.True or JsonValueKind.False;
                    settings = accepted ? settings with { StartListeningOnLaunch = value.GetBoolean() } : settings;
                    break;

                case "hascompletedsetup":
                    accepted = value.ValueKind is JsonValueKind.True or JsonValueKind.False;
                    settings = accepted ? settings with { HasCompletedSetup = value.GetBoolean() } : settings;
                    break;

                case "hidemainwindowinminimode":
                    accepted = value.ValueKind is JsonValueKind.True or JsonValueKind.False;
                    settings = accepted ? settings with { HideMainWindowInMiniMode = value.GetBoolean() } : settings;
                    break;

                case "minialwaysontop":
                    accepted = value.ValueKind is JsonValueKind.True or JsonValueKind.False;
                    settings = accepted ? settings with { MiniAlwaysOnTop = value.GetBoolean() } : settings;
                    break;

                case "miniopacity":
                    accepted = value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var opacity) && AppSettings.IsValidMiniOpacity(opacity);
                    settings = accepted ? settings with { MiniOpacity = value.GetDouble() } : settings;
                    break;

                case "miniplacements":
                    // Entry by entry: one damaged position must not cost the positions of the other monitor arrangements.
                    // A non-array is rejected as a whole; an array with a bad entry keeps the good ones and still reports the field.
                    accepted = TryReadPlacements(value, out var placements);
                    settings = placements is null ? settings : settings with { MiniPlacements = placements };
                    break;

                default:
                    // Clone: the element points into the document, which is disposed when this method returns.
                    (unknown ??= [])[name] = value.Clone();
                    break;
            }

            if (!accepted)
            {
                rejected.Add(name);
            }
        }

        return new SettingsReadResult(settings with { ExtensionData = unknown }, fileVersion, rejected);
    }

    /// <summary>
    /// Reads the list of remembered mini-window positions. Returns false when the value is not an array or when any entry was unusable;
    /// <paramref name="placements"/> then holds whatever could be kept (null when nothing could).
    /// </summary>
    private static bool TryReadPlacements(JsonElement value, out IReadOnlyList<MiniWindowPlacement>? placements)
    {
        placements = null;
        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var kept = new List<MiniWindowPlacement>();
        var allUsable = true;
        foreach (var entry in value.EnumerateArray())
        {
            if (TryReadPlacement(entry, out var placement))
            {
                kept.Add(placement);
            }
            else
            {
                allUsable = false;
            }
        }

        placements = kept;
        return allUsable;
    }

    private static bool TryReadPlacement(JsonElement entry, out MiniWindowPlacement placement)
    {
        placement = null!;
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? setup = null;
        double left = double.NaN, top = double.NaN, width = double.NaN, height = double.NaN;
        foreach (var property in entry.EnumerateObject())
        {
            var number = property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var parsed) ? parsed : double.NaN;
            switch (property.Name.ToLowerInvariant())
            {
                case "setup": setup = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null; break;
                case "left": left = number; break;
                case "top": top = number; break;
                case "width": width = number; break;
                case "height": height = number; break;
            }
        }

        var candidate = new MiniWindowPlacement(setup ?? "", left, top, width, height);
        if (!candidate.IsUsable)
        {
            return false;
        }

        placement = candidate;
        return true;
    }

    /// <summary>A JSON number that is a whole number fitting an int. (<see cref="JsonElement.TryGetInt32"/> alone THROWS for a non-number, so the kind is checked first.)</summary>
    private static bool TryReadInt(JsonElement value, out int result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result);
    }

    /// <summary>
    /// Accepts a defined enum NAME in any casing, or a number that is a defined value. Not <see cref="Enum.TryParse{TEnum}(string?, out TEnum)"/>
    /// on its own: that also accepts any numeric string ("42") as a valid but undefined value.
    /// </summary>
    private static bool TryReadEnum<TEnum>(JsonElement value, out TEnum result) where TEnum : struct, Enum
    {
        result = default;

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString();
                foreach (var name in Enum.GetNames<TEnum>())
                {
                    if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                    {
                        result = Enum.Parse<TEnum>(name);
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Number when TryReadInt(value, out var number) && Enum.IsDefined(typeof(TEnum), number):
                result = (TEnum)Enum.ToObject(typeof(TEnum), number);
                return true;

            default:
                return false;
        }
    }
}
