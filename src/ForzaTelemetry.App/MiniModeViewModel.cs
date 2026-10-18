using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;

namespace ForzaTelemetry.App;

/// <summary>
/// State of the compact always-on-top window: whether it is open, whether it stays on top, how see-through it is, and where it sits.
/// </summary>
/// <remarks>
/// <b>No second data path.</b> The mini window shows the SAME <see cref="DashboardViewModel"/> and <see cref="LapsViewModel"/> instances the
/// main window shows: this class only exposes them. The shell's 20 Hz refresh already feeds them, so the mini window costs nothing per tick
/// while it is closed (no view exists, nothing is bound), and while it is open it costs one more set of bindings on values that are updated anyway.
/// <para>
/// <b>What persists, and when.</b> Pinning is one click, so it is saved at once. Opacity is a slider that fires on every pixel of a drag:
/// each change would rewrite the settings file, so it is only held in memory while dragging and saved together with the position when the
/// window closes (<see cref="SaveWindowState"/>). A crash mid-session therefore forgets a slider move, which is cheaper than a disk write per pixel.
/// </para>
/// <para>
/// <b>Opening and closing is a state, not a window call.</b> <see cref="IsOpen"/> is what commands, the keyboard and the window's own close
/// button change; the window controller (<c>MiniModeController</c>) watches it and shows or closes the real window. That keeps this class free of
/// WPF windows, so open/close, persistence and placement are all unit-tested.
/// </para>
/// </remarks>
public sealed partial class MiniModeViewModel : ObservableObject
{
    private readonly SettingsService? _settings;

    /// <param name="dashboard">The dashboard values the window shows (gear, speed, shift lights); the very same instance the main window binds.</param>
    /// <param name="laps">The laps values (live delta, running lap time); null in tests that do not care, the window then shows only gear and speed.</param>
    /// <param name="settings">Where the preferences live; optional so a test or the designer can run on the defaults.</param>
    public MiniModeViewModel(DashboardViewModel dashboard, LapsViewModel? laps = null, SettingsService? settings = null)
    {
        Dashboard = dashboard;
        Laps = laps;
        _settings = settings;

        var current = settings?.Current ?? AppSettings.Default;
        _isTopmost = current.MiniAlwaysOnTop;
        _opacity = MiniModeGeometry.ClampOpacity(current.MiniOpacity);

        if (settings is not null)
        {
            settings.Changed += OnSettingsChanged;
        }
    }

    /// <summary>The dashboard values (gear, speed, unit, shift lights) the window binds.</summary>
    public DashboardViewModel Dashboard { get; }

    /// <summary>The lap values (live delta, lap clock) the window binds; null when the shell has no Laps page.</summary>
    public LapsViewModel? Laps { get; }

    /// <summary>True while the mini window should be on screen. The controller shows and closes the window from this.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>The window stays above all others. Pinned by default: a HUD you have to keep re-raising is no HUD.</summary>
    [ObservableProperty]
    private bool _isTopmost;

    private double _opacity;

    /// <summary>
    /// How opaque the window is, <see cref="AppSettings.MinMiniOpacity"/>..1. A hand-written property (not a generated one) because the value is
    /// clamped BEFORE it is stored: a generated "changing" hook runs before the assignment, so a clamp done there would be overwritten by it.
    /// </summary>
    public double Opacity
    {
        get => _opacity;
        set
        {
            if (SetProperty(ref _opacity, MiniModeGeometry.ClampOpacity(value)))
            {
                // The tooltip and the spoken value are built from the opacity, so they announce their own change.
                OnPropertyChanged(nameof(OpacityPercent));
                OnPropertyChanged(nameof(OpacityText));
            }
        }
    }

    /// <summary>The opacity as a whole percentage (60..100), for the slider's tooltip and its spoken value.</summary>
    public int OpacityPercent => (int)Math.Round(Opacity * 100, MidpointRounding.AwayFromZero);

    /// <summary>The slider's value as a sentence, for example <c>Opacity 85%</c>.</summary>
    public string OpacityText => string.Format(CultureInfo.CurrentCulture, MiniStrings.MiniOpacityFormat, OpacityPercent);

    /// <summary>
    /// True when opening the mini window should minimize the main window (the "Hide main window while mini mode is open" setting). The
    /// controller reads it ONCE, at the moment the mini window opens, and remembers what it did: a change made while the mini window is open
    /// applies to the next opening, and closing never re-reads it (otherwise the same session could minimize the main window and then
    /// decline to bring it back, or the reverse).
    /// </summary>
    public bool HideMainWindow => (_settings?.Current ?? AppSettings.Default).HideMainWindowInMiniMode;

    /// <summary>Open when closed, close when open: the Ctrl+M shortcut and the title-bar button.</summary>
    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    /// <summary>Closes the window (Esc, the window's own close button).</summary>
    [RelayCommand]
    private void Close() => IsOpen = false;

    /// <summary>Flips <see cref="IsTopmost"/> (the pin button).</summary>
    [RelayCommand]
    private void ToggleTopmost() => IsTopmost = !IsTopmost;

    // CommunityToolkit.Mvvm calls this after the value really changed, which is where "the user pinned it" becomes "the setting changed".
    partial void OnIsTopmostChanged(bool value) => _settings?.Update(s => s with { MiniAlwaysOnTop = value });

    /// <summary>
    /// The remembered position and size for the monitor arrangement <paramref name="setup"/> (see <see cref="MiniModeGeometry.SetupKey"/>), or
    /// null when the window was never moved on this arrangement. The rectangle mixes units on purpose: Left/Top are physical pixels, Width/Height are
    /// device-independent units (see <see cref="MiniWindowPlacement"/>).
    /// </summary>
    public Rect? SavedPlacement(string setup) =>
        (_settings?.Current ?? AppSettings.Default).MiniPlacements?.LastOrDefault(p => p.Setup == setup) is { } placement
            ? new Rect(placement.Left, placement.Top, placement.Width, placement.Height)
            : null;

    /// <summary>
    /// Saves what a closing window knows: where it was on this monitor arrangement and how see-through it is. One settings write, however
    /// much the user moved or dragged meanwhile. The newest entry goes last and the list keeps only the latest few arrangements.
    /// </summary>
    /// <param name="setup">The monitor arrangement the position belongs to.</param>
    /// <param name="bounds">The window's NORMAL rectangle: Left/Top in physical pixels, Width/Height in device-independent units.</param>
    public void SaveWindowState(string setup, Rect bounds)
    {
        if (_settings is null)
        {
            return;
        }

        var placement = new MiniWindowPlacement(setup, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        _settings.Update(s => s with
        {
            MiniOpacity = Opacity,
            MiniPlacements = (s.MiniPlacements ?? [])
                .Where(p => p.Setup != setup)
                .Append(placement)
                .ToList()
        });
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        // The pin may be changed from elsewhere (the settings file, a future Settings control): follow it. Opacity is deliberately NOT
        // pulled back while the window is open, or a save made by this very window would fight the slider the user is still holding.
        IsTopmost = e.Current.MiniAlwaysOnTop;

        if (e.Current.HideMainWindowInMiniMode != e.Previous.HideMainWindowInMiniMode)
        {
            OnPropertyChanged(nameof(HideMainWindow));
        }
    }
}
