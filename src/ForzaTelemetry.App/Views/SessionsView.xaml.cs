using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ForzaTelemetry.App.Views;

/// <summary>
/// The Sessions screen. Everything it shows and does is in <see cref="SessionsViewModel"/>; this code-behind only handles the
/// three things a view model cannot do: reading the folder when the page opens, and moving keyboard focus in and out of the
/// inline rename box (focus is a UI concept with no view-model equivalent).
/// </summary>
public partial class SessionsView : UserControl
{
    public SessionsView() => InitializeComponent();

    /// <summary>
    /// The shell rebuilds a page's view every time its tab is opened (see MainWindow), so Loaded fires on every visit: the
    /// library is read again each time, which also picks up files the user added or removed in Explorer meanwhile.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is SessionsViewModel viewModel)
        {
            viewModel.RefreshLibraryCommand.Execute(null);
        }
    }

    /// <summary>When the rename box appears, focus it with the whole name selected, as every file manager does.</summary>
    private void OnRenameBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox box && box.IsVisible)
        {
            // Dispatched: the box has just become visible and is not focusable until layout has run.
            box.Dispatcher.BeginInvoke(
                () =>
                {
                    box.Focus();
                    box.SelectAll();
                },
                System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    /// <summary>
    /// Leaving the box (a click elsewhere, Tab) cancels the edit instead of saving it: a rename is only made on purpose (Enter),
    /// never by accident. Collapsing the box after Enter or Esc also ends here, where the row is no longer editing.
    /// </summary>
    private void OnRenameBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecordingRowViewModel { IsEditing: true } row })
        {
            row.CancelRenameCommand.Execute(null);
        }
    }
}
