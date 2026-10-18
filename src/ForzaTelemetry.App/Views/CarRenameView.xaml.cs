using System.Windows;
using System.Windows.Controls;

namespace ForzaTelemetry.App.Views;

/// <summary>
/// The car rename box. Everything it shows and does is in <see cref="CarRenameViewModel"/>; this code-behind only moves keyboard
/// focus into the text box, with the current name selected, when the box appears (focus has no view-model equivalent).
/// </summary>
public partial class CarRenameView : UserControl
{
    public CarRenameView() => InitializeComponent();

    /// <summary>
    /// The box was just made visible, so its text box is not focusable until layout has run: the focus is requested at Input priority.
    /// Selecting the whole name is what every rename box does, so typing replaces it and Enter alone keeps it.
    /// </summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            Dispatcher.BeginInvoke(
                () =>
                {
                    NameBox.Focus();
                    NameBox.SelectAll();
                },
                System.Windows.Threading.DispatcherPriority.Input);
        }
    }
}
