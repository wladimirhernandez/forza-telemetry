//------------------------------------------------------------------------------
// Hand-maintained strongly typed accessor for CarNameStrings.resx (same shape as SessionsStrings).
// It is a separate resource set so the car rename box does not touch Strings.resx. Add a property
// here whenever a key is added to CarNameStrings.resx (and to CarNameStrings.es.resx).
//------------------------------------------------------------------------------

#nullable enable

namespace ForzaTelemetry.App.Resources;

/// <summary>CarNameStrings (neutral English; Spanish in CarNameStrings.es.resx).</summary>
public static class CarNameStrings
{
    private static global::System.Resources.ResourceManager? _resourceManager;

    /// <summary>The resource manager for <c>CarNameStrings.resx</c> and its satellite cultures.</summary>
    public static global::System.Resources.ResourceManager ResourceManager =>
        _resourceManager ??= new global::System.Resources.ResourceManager(
            "ForzaTelemetry.App.Resources.CarNameStrings", typeof(CarNameStrings).Assembly);

    /// <summary>Culture override for lookups; null uses the current UI culture (as set by LanguageService).</summary>
    public static global::System.Globalization.CultureInfo? Culture { get; set; }

    /// <summary>"Name car #{0}"</summary>
    public static string CarRenameTitle => ResourceManager.GetString("CarRenameTitle", Culture)!;

    /// <summary>"A nickname only for you. It shows on every screen and in the recordings list."</summary>
    public static string CarRenameHint => ResourceManager.GetString("CarRenameHint", Culture)!;

    /// <summary>"Car name"</summary>
    public static string CarRenameBoxName => ResourceManager.GetString("CarRenameBoxName", Culture)!;

    /// <summary>"Save"</summary>
    public static string CarRenameSave => ResourceManager.GetString("CarRenameSave", Culture)!;

    /// <summary>"Cancel"</summary>
    public static string CarRenameCancel => ResourceManager.GetString("CarRenameCancel", Culture)!;

    /// <summary>"Use the default name"</summary>
    public static string CarRenameUseDefault => ResourceManager.GetString("CarRenameUseDefault", Culture)!;

    /// <summary>"Type a name."</summary>
    public static string CarNameEmpty => ResourceManager.GetString("CarNameEmpty", Culture)!;

    /// <summary>"Use at most {0} characters."</summary>
    public static string CarNameTooLong => ResourceManager.GetString("CarNameTooLong", Culture)!;

    /// <summary>"Rename this car"</summary>
    public static string CarRenameTooltip => ResourceManager.GetString("CarRenameTooltip", Culture)!;

    /// <summary>"Rename {0}"</summary>
    public static string CarRenameRowName => ResourceManager.GetString("CarRenameRowName", Culture)!;
}
