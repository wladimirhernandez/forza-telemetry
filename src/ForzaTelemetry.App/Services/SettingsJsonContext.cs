using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaTelemetry.App.Services;

/// <summary>
/// Source-generated System.Text.Json metadata used to WRITE <see cref="AppSettings"/> (reading is done by
/// <see cref="AppSettingsReader"/>, which tolerates a bad value in one field).
/// </summary>
/// <remarks>
/// "Source generation" means the compiler writes the serializer for the type at BUILD time (see the attribute below)
/// instead of System.Text.Json inspecting the type with reflection at runtime. Result: faster startup, no reflection
/// metadata that trimming/AOT could break, and a typo in the type is a build error. The options live in the attribute:
/// indented output (the file is meant to be readable and hand-editable) and camelCase property names (the JSON convention).
/// <para>
/// <b>Enums are written by NAME in camelCase</b> (<c>"mph"</c>, <c>"fahrenheit"</c>), so the file does not depend on enum
/// numbering and its values look like its keys. The shortcut <c>UseStringEnumConverter = true</c> would write the C# names
/// verbatim (<c>"Mph"</c>) because that switch cannot take a naming policy; a converter instance can, which is why each
/// enum gets its own tiny <see cref="CamelCaseEnumConverter{TEnum}"/> subclass: attribute arguments must be types with a
/// parameterless constructor, and the generic base class needs the policy passed to its constructor.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    Converters =
    [
        typeof(CamelCaseEnumConverter<SpeedUnit>),
        typeof(CamelCaseEnumConverter<TemperatureUnit>),
        typeof(CamelCaseEnumConverter<AppLanguage>)
    ])]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>Writes an enum as its camelCase name (<c>Fahrenheit</c> becomes <c>"fahrenheit"</c>).</summary>
/// <typeparam name="TEnum">The enum type; one closed subclass per enum is listed in <see cref="SettingsJsonContext"/>.</typeparam>
internal sealed class CamelCaseEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.CamelCase)
    where TEnum : struct, Enum;
