namespace ForzaTelemetry.App;

/// <summary>
/// A screen that is not built yet: an icon, a title and one sentence about what the screen will do. One class serves
/// every such tab because they differ only in data, which is why a single <c>DataTemplate</c> (and a single
/// <c>PlaceholderView</c>) can show all of them.
/// </summary>
/// <remarks>Immutable on purpose: nothing about a placeholder changes while the app runs, so there is nothing to notify.</remarks>
/// <param name="id">Which tab this placeholder fills.</param>
/// <param name="title">Localized screen title.</param>
/// <param name="description">Localized sentence saying what the screen will show.</param>
/// <param name="iconPathData">Path mini-language outline for the icon (see <see cref="PageIcons"/>).</param>
public sealed class PlaceholderPageViewModel(PageId id, string title, string description, string iconPathData)
{
    public PageId Id { get; } = id;

    public string Title { get; } = title;

    public string Description { get; } = description;

    public string IconPathData { get; } = iconPathData;
}
