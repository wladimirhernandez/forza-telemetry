using System.ComponentModel;
using System.Windows;

namespace ForzaTelemetry.App;

/// <summary>
/// Shows and closes the real mini window when <see cref="MiniModeViewModel.IsOpen"/> changes, and hides and brings back the main window
/// around it (the rules are in <see cref="MainWindowHidePolicy"/>). It is the only code that touches both windows, which keeps the view model
/// free of WPF windows (and testable).
/// </summary>
/// <remarks>
/// <b>The mini window can close without us asking</b> (Alt+F4, the window's own close button, Windows shutting down). Its <c>Closed</c> event is
/// therefore the single place that clears the window, flips <see cref="MiniModeViewModel.IsOpen"/> to false and restores the main window; closing
/// from the view model just calls <see cref="Window.Close"/> and lets that same event do the rest.
/// </remarks>
internal sealed class MiniModeController
{
    private readonly Window _main;
    private readonly MiniModeViewModel _viewModel;
    private readonly MainWindowHidePolicy _policy = new();

    private MiniWindow? _window;
    private bool _shuttingDown;

    public MiniModeController(Window main, MiniModeViewModel viewModel)
    {
        _main = main;
        _viewModel = viewModel;
        viewModel.PropertyChanged += OnViewModelChanged;

        // Every change of the main window's state is reported to the policy, so it can tell "the state we set" from "the user took over".
        main.StateChanged += (_, _) => _policy.OnMainWindowStateChanged(_main.WindowState);

        // The app ends with the main window, so a mini window left open must not keep the process alive on its own.
        main.Closed += (_, _) => Shutdown();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MiniModeViewModel.IsOpen) || _shuttingDown)
        {
            return;
        }

        if (_viewModel.IsOpen)
        {
            Open();
        }
        else
        {
            _window?.Close();
        }
    }

    private void Open()
    {
        if (_window is not null)
        {
            return;
        }

        var window = new MiniWindow(_viewModel);
        window.Closed += OnMiniClosed;
        _window = window;
        window.Show();
        window.Activate();

        // After the mini window is up: minimizing first would hand the focus to whatever window is behind the app before mini mode can take it.
        // The setting is read here, once (see MainWindowHidePolicy).
        if (_policy.BeginOpen(_viewModel.HideMainWindow, _main.WindowState))
        {
            _main.WindowState = WindowState.Minimized;
        }
    }

    private void OnMiniClosed(object? sender, EventArgs e)
    {
        _window = null;

        // A close the view model did not ask for (Alt+F4) must still leave it consistent; a no-op when it already said false.
        _viewModel.IsOpen = false;

        var restoreTo = _policy.EndClose(_main.WindowState);
        if (_shuttingDown || restoreTo is null)
        {
            return;
        }

        _main.WindowState = restoreTo.Value;
        _main.Activate();
    }

    private void Shutdown()
    {
        _shuttingDown = true;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _window?.Close();
    }
}
