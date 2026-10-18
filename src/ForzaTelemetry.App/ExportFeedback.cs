using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using ForzaTelemetry.App.Resources;

namespace ForzaTelemetry.App;

/// <summary>
/// The one-line message next to an Export button: what just happened ("Saved laps-2026-10-18.csv", "Could not save ...").
/// Shared by the pages that export so the wording, and the way a screen reader is told, are the same everywhere.
/// </summary>
/// <remarks>
/// The view shows <see cref="Text"/> in a live region (announced when it changes) and colors it red when <see cref="IsError"/>.
/// The full path goes in <see cref="Tooltip"/>: the line itself only has room for the file name.
/// </remarks>
public sealed partial class ExportFeedback : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText))]
    private string _text = "";

    [ObservableProperty]
    private bool _isError;

    /// <summary>The full path (or the full error) behind the line, for its tooltip.</summary>
    [ObservableProperty]
    private string _tooltip = "";

    public bool HasText => Text.Length > 0;

    /// <summary>The file was written.</summary>
    public void Saved(string path) => Show(
        string.Format(CultureInfo.CurrentCulture, ExportStrings.ExportSaved, Path.GetFileName(path)), path, isError: false);

    /// <summary>The file could not be written.</summary>
    public void Failed(string path, string reason) => Show(
        string.Format(CultureInfo.CurrentCulture, ExportStrings.ExportFailed, Path.GetFileName(path), reason), null, isError: true);

    /// <summary>The export was stopped before it finished (nothing was written, the old file is untouched).</summary>
    public void Cancelled() => Show(ExportStrings.ExportCancelled, "", isError: false);

    /// <summary>Empties the line (a new action starts, or the data it was about is gone).</summary>
    public void Clear() => Show("", "", isError: false);

    private void Show(string text, string? tooltip, bool isError)
    {
        IsError = isError;
        Tooltip = tooltip ?? text; // for a failure the line is trimmed to fit, so the tooltip carries all of it
        Text = text;
    }
}
