namespace ForzaTelemetry.App;

/// <summary>
/// One selectable choice in a Settings control: the value to store and the localized text to show. A tiny generic record so
/// the segmented unit pickers and the language combo box are all data-driven (<c>ItemsSource</c> of these,
/// <c>SelectedValuePath="Value"</c>, the <c>Label</c> shown) with no converters.
/// </summary>
public sealed record ChoiceOption<T>(T Value, string Label)
{
    // WPF falls back to ToString() whenever it needs text for an item and has no template (UI Automation names, type-ahead
    // search in a combo box), so make that text the label instead of the record's debug dump.
    public override string ToString() => Label;
}
