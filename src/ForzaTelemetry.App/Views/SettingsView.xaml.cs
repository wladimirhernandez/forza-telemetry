using System.Windows.Controls;
using System.Windows.Input;

namespace ForzaTelemetry.App.Views;

/// <summary>The Settings screen: connection setup and Data Out guide, units, language, startup, diagnostics and about.</summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>
    /// The port box only accepts digits as they are typed: <c>PreviewTextInput</c> fires BEFORE the text reaches the box
    /// (the "Preview" events tunnel down from the window to the control), so marking it handled cancels the keystroke.
    /// Pasted text bypasses this event, which is why the view model validates the final text as well.
    /// </summary>
    private void OnPortPreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsAsciiDigit);
}
