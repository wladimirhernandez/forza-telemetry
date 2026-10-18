namespace ForzaTelemetry.App.Services;

/// <summary>Raised after a settings change: the value before and after, so a listener can react only to what changed.</summary>
public sealed class SettingsChangedEventArgs(AppSettings previous, AppSettings current) : EventArgs
{
    public AppSettings Previous { get; } = previous;

    public AppSettings Current { get; } = current;
}

/// <summary>
/// The one live copy of the user's <see cref="AppSettings"/>: other classes read <see cref="Current"/>, change it through
/// <see cref="Update"/>, and subscribe to <see cref="Changed"/> to react.
/// </summary>
/// <remarks>
/// <b>Why a service in the middle.</b> Several unrelated parts care about the same values (the UDP listener factory reads the
/// port, the units service reads the units, the shell restarts the listener when the port changes, the Settings screen edits
/// all of them). Without one owner each would keep its own copy and they would drift apart. With it, "change a setting" is
/// one call that updates the value, saves it to disk and tells everyone, in that order.
/// <para>
/// Not thread-safe by design: like the view models it is only used on the UI thread. The UDP listener factory reads
/// <see cref="Current"/> from there too (Start runs on the UI thread).
/// </para>
/// </remarks>
public sealed class SettingsService
{
    private readonly ISettingsStore _store;

    /// <param name="store">Where changes are saved.</param>
    /// <param name="initial">The settings loaded at startup (sanitized here, so callers cannot smuggle in an invalid value).</param>
    public SettingsService(ISettingsStore store, AppSettings initial)
    {
        _store = store;
        Current = initial.Sanitized();
    }

    /// <summary>The settings in effect right now.</summary>
    public AppSettings Current { get; private set; }

    /// <summary>Raised after <see cref="Current"/> changed and the change was saved (or the save failed and was logged).</summary>
    public event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <summary>
    /// Replaces the settings with <c>change(Current)</c>: sanitizes, ignores a no-op, saves and notifies. A failed save
    /// does not undo the change: the user's choice still applies for this run (the store logged why it was not kept).
    /// </summary>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        var previous = Current;
        var next = change(previous).Sanitized();
        if (next == previous)
        {
            return;
        }

        Current = next;
        _store.TrySave(next);
        Changed?.Invoke(this, new SettingsChangedEventArgs(previous, next));
    }
}
