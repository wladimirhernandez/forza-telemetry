using System.Text.Json.Serialization;

namespace ForzaTelemetry.App.Services;

/// <summary>
/// Source-generated System.Text.Json metadata for <see cref="AppSettings"/>.
/// </summary>
/// <remarks>
/// "Source generation" means the compiler writes the serializer for the type at BUILD time (see the attribute below)
/// instead of System.Text.Json inspecting the type with reflection at runtime. Result: faster startup, no reflection
/// metadata that trimming/AOT could break, and a typo in the type is a build error. The options live in the attribute:
/// indented output (the file is meant to be readable and hand-editable), camelCase names (the JSON convention), and enums
/// written by NAME ("mph") so the file does not depend on enum numbering.
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
