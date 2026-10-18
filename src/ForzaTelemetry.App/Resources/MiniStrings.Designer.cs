//------------------------------------------------------------------------------
// Hand-maintained strongly typed accessor for MiniStrings.resx (same shape as ExportStrings).
// The compact always-on-top window and its settings.
// Add a property here whenever a key is added to MiniStrings.resx (and to MiniStrings.es.resx).
//------------------------------------------------------------------------------

#nullable enable

namespace ForzaTelemetry.App.Resources;

/// <summary>Localized text of The compact always-on-top window and its settings. (neutral English; Spanish in MiniStrings.es.resx).</summary>
public static class MiniStrings
{
    private static global::System.Resources.ResourceManager? _resourceManager;

    /// <summary>The resource manager for <c>MiniStrings.resx</c> and its satellite cultures.</summary>
    public static global::System.Resources.ResourceManager ResourceManager =>
        _resourceManager ??= new global::System.Resources.ResourceManager(
            "ForzaTelemetry.App.Resources.MiniStrings", typeof(MiniStrings).Assembly);

    /// <summary>Culture override for lookups; null uses the current UI culture (as set by LanguageService).</summary>
    public static global::System.Globalization.CultureInfo? Culture { get; set; }

    /// <summary>"Mini mode (Ctrl+M)"</summary>
    public static string MiniModeTooltip => ResourceManager.GetString("MiniModeTooltip", Culture)!;

    /// <summary>"Forza Telemetry mini mode"</summary>
    public static string MiniWindowTitle => ResourceManager.GetString("MiniWindowTitle", Culture)!;

    /// <summary>"Keep on top of other windows"</summary>
    public static string MiniPinTooltip => ResourceManager.GetString("MiniPinTooltip", Culture)!;

    /// <summary>"Always on top"</summary>
    public static string MiniPinName => ResourceManager.GetString("MiniPinName", Culture)!;

    /// <summary>"Opacity"</summary>
    public static string MiniOpacityName => ResourceManager.GetString("MiniOpacityName", Culture)!;

    /// <summary>"Opacity {0}%"</summary>
    public static string MiniOpacityFormat => ResourceManager.GetString("MiniOpacityFormat", Culture)!;

    /// <summary>"Close mini mode (Esc)"</summary>
    public static string MiniCloseTooltip => ResourceManager.GetString("MiniCloseTooltip", Culture)!;

    /// <summary>"Mini mode"</summary>
    public static string MiniHideMainTitle => ResourceManager.GetString("MiniHideMainTitle", Culture)!;

    /// <summary>"Hide main window while mini mode is open"</summary>
    public static string MiniHideMainLabel => ResourceManager.GetString("MiniHideMainLabel", Culture)!;

    /// <summary>"The main window minimizes when mini mode opens and returns when you close it. Press Ctrl+M anywhere in the app to open it."</summary>
    public static string MiniHideMainHint => ResourceManager.GetString("MiniHideMainHint", Culture)!;
}
