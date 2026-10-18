using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ForzaTelemetry.App.Views;

/// <summary>
/// The "Connect your game" card. Everything it shows and does is in <see cref="ConnectionGuideViewModel"/>; this code-behind
/// only moves keyboard focus into the card when it appears (focus is a UI concept with no view-model equivalent).
/// </summary>
public partial class ConnectionGuideView : UserControl
{
    public ConnectionGuideView() => InitializeComponent();

    /// <summary>
    /// When the guide appears, keyboard focus moves into it: otherwise it would stay on the control that opened the guide, now
    /// hidden behind the scrim, and Esc and Tab would act on the wrong place. Dispatched at Input priority because the card has
    /// just become visible and its buttons are not focusable until layout has run.
    /// </summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            Dispatcher.BeginInvoke(
                () =>
                {
                    Focusable = true;
                    Focus();
                    MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                },
                System.Windows.Threading.DispatcherPriority.Input);
        }
    }
}
