//------------------------------------------------------------------------------
// Hand-maintained strongly typed accessor for ExportStrings.resx (same shape as SessionsStrings).
// It is a separate resource set so the CSV export does not touch Strings.resx. Add a property
// here whenever a key is added to ExportStrings.resx (and to ExportStrings.es.resx).
//------------------------------------------------------------------------------

#nullable enable

namespace ForzaTelemetry.App.Resources;

/// <summary>Localized text of the CSV exports (neutral English; Spanish in ExportStrings.es.resx).</summary>
public static class ExportStrings
{
    private static global::System.Resources.ResourceManager? _resourceManager;

    /// <summary>The resource manager for <c>ExportStrings.resx</c> and its satellite cultures.</summary>
    public static global::System.Resources.ResourceManager ResourceManager =>
        _resourceManager ??= new global::System.Resources.ResourceManager(
            "ForzaTelemetry.App.Resources.ExportStrings", typeof(ExportStrings).Assembly);

    /// <summary>Culture override for lookups; null uses the current UI culture (as set by LanguageService).</summary>
    public static global::System.Globalization.CultureInfo? Culture { get; set; }

    /// <summary>"Export CSV"</summary>
    public static string ExportButton => ResourceManager.GetString("ExportButton", Culture)!;

    /// <summary>"Save this session's laps as a CSV file for a spreadsheet"</summary>
    public static string ExportLapsTooltip => ResourceManager.GetString("ExportLapsTooltip", Culture)!;

    /// <summary>"Finish a lap to be able to export"</summary>
    public static string ExportLapsDisabledTooltip => ResourceManager.GetString("ExportLapsDisabledTooltip", Culture)!;

    /// <summary>"Save every car's best results as a CSV file for a spreadsheet"</summary>
    public static string ExportPerformanceTooltip => ResourceManager.GetString("ExportPerformanceTooltip", Culture)!;

    /// <summary>"There are no results to export yet"</summary>
    public static string ExportPerformanceDisabledTooltip => ResourceManager.GetString("ExportPerformanceDisabledTooltip", Culture)!;

    /// <summary>"Convert this recording to a CSV file, one row per packet"</summary>
    public static string ExportRecordingTooltip => ResourceManager.GetString("ExportRecordingTooltip", Culture)!;

    /// <summary>"Export {0} to CSV"</summary>
    public static string ExportRecordingNamed => ResourceManager.GetString("ExportRecordingNamed", Culture)!;

    /// <summary>"Another export is still running"</summary>
    public static string ExportBusyTooltip => ResourceManager.GetString("ExportBusyTooltip", Culture)!;

    /// <summary>"Export to CSV"</summary>
    public static string ExportDialogTitle => ResourceManager.GetString("ExportDialogTitle", Culture)!;

    /// <summary>"CSV files"</summary>
    public static string ExportFilterLabel => ResourceManager.GetString("ExportFilterLabel", Culture)!;

    /// <summary>"laps"</summary>
    public static string ExportLapsFileName => ResourceManager.GetString("ExportLapsFileName", Culture)!;

    /// <summary>"performance"</summary>
    public static string ExportPerformanceFileName => ResourceManager.GetString("ExportPerformanceFileName", Culture)!;

    /// <summary>"Saved {0}"</summary>
    public static string ExportSaved => ResourceManager.GetString("ExportSaved", Culture)!;

    /// <summary>"Saved {0} ({1} packets)"</summary>
    public static string ExportSavedPackets => ResourceManager.GetString("ExportSavedPackets", Culture)!;

    /// <summary>"Saved {0} ({1} packets). The recording was cut off, so only its complete packets were exported."</summary>
    public static string ExportSavedPacketsTruncated => ResourceManager.GetString("ExportSavedPacketsTruncated", Culture)!;

    /// <summary>"Could not save {0}: {1}"</summary>
    public static string ExportFailed => ResourceManager.GetString("ExportFailed", Culture)!;

    /// <summary>"Export cancelled"</summary>
    public static string ExportCancelled => ResourceManager.GetString("ExportCancelled", Culture)!;

    /// <summary>"Exporting {0}… {1}%"</summary>
    public static string ExportProgressFormat => ResourceManager.GetString("ExportProgressFormat", Culture)!;

    /// <summary>"Export progress"</summary>
    public static string ExportProgressName => ResourceManager.GetString("ExportProgressName", Culture)!;

    /// <summary>"Stop the export and discard the partial file"</summary>
    public static string ExportCancelTooltip => ResourceManager.GetString("ExportCancelTooltip", Culture)!;
}
