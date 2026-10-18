using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;

namespace ForzaTelemetry.App.Views;

/// <summary>A themed yes/no dialog (see <see cref="Services.WpfConfirmationService"/>). The safe button is the default.</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirmText, string cancelText)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;

        // The window has no OS title bar, so give screen readers the dialog's name explicitly.
        AutomationProperties.SetName(this, title);
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>The card is the only visible surface, so dragging it moves the dialog (like the main window's title bar).</summary>
    private void OnDragMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
        {
            DragMove();
        }
    }
}
