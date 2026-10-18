namespace ForzaTelemetry.App;

/// <summary>
/// One entry of the top tab strip: which screen it opens, its localized label and the view model that backs the screen.
/// </summary>
/// <param name="Id">Which screen this is.</param>
/// <param name="Title">Localized tab label (also the screen reader name).</param>
/// <param name="Shortcut">Keyboard shortcut text such as <c>Ctrl+3</c>, shown in the tooltip and exposed to screen readers.</param>
/// <param name="Page">
/// The page's view model. It is typed <see cref="object"/> on purpose: the view for it is NOT chosen here but by the
/// implicit <c>DataTemplate</c> in <c>MainWindow.xaml</c>, which maps a view-model TYPE to a view. That keeps the view
/// models free of any reference to WPF.
/// </param>
public sealed record NavigationItem(PageId Id, string Title, string Shortcut, object Page)
{
    /// <summary>Tooltip text: the label plus its shortcut, for example <c>Laps (Ctrl+3)</c>.</summary>
    public string ToolTip => $"{Title} ({Shortcut})";
}
