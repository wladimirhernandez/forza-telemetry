using System.Reflection;
using ForzaTelemetry.App.Resources;

namespace ForzaTelemetry.App;

/// <summary>What the app calls itself and which version/license it ships under (shown in Settings, written to the log).</summary>
public static class AppInfo
{
    /// <summary>
    /// The product name, from the ONE place it is defined: the <c>AppTitle</c> string in Strings.resx (and its Spanish
    /// twin). "Forza Telemetry" is a WORKING TITLE: the Forza trademark is not meant to be the final product name, so
    /// renaming the app is a change to those two resource values only; the window title, the title bar, the About card and
    /// the log lines all read this.
    /// </summary>
    public static string Name => Strings.AppTitle;

    /// <summary>The license the source is published under (a proper noun, so it is not translated).</summary>
    public const string LicenseName = "PolyForm Strict 1.0.0";

    /// <summary>
    /// The assembly's informational version without the build metadata (<c>1.0.0+abc123</c> becomes <c>1.0.0</c>), or the
    /// plain assembly version when no informational version is stamped.
    /// </summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = typeof(AppInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
