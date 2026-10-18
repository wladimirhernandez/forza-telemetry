using Microsoft.Extensions.Logging;

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
/// <b>When the file could not be read.</b> If the settings file exists but could not be read (<see cref="ISettingsStore.LoadFailedToRead"/>,
/// or the whole load threw), the defaults in memory are a stand-in, not the user's choices. Saving them would overwrite the real file, so
/// <see cref="IsPersistenceSuspended"/> is set: changes still apply for the session, nothing is written, and the shell does not open the
/// first-run guide (it would be shown to someone who set up long ago). The next start reads the file again.
/// </para>
/// <para>
/// Not thread-safe by design: like the view models it is only used on the UI thread. The UDP listener factory reads
/// <see cref="Current"/> from there too (Start runs on the UI thread).
/// </para>
/// </remarks>
public sealed class SettingsService
{
    private readonly ISettingsStore _store;
    private readonly ILogger? _logger;

    /// <param name="store">Where changes are saved.</param>
    /// <param name="initial">The settings loaded at startup (sanitized here, so callers cannot smuggle in an invalid value).</param>
    /// <param name="persistenceSuspended">True when <paramref name="initial"/> is only a fallback for a file that could not be read: nothing is saved.</param>
    /// <param name="logger">Receives the one warning when a write is skipped; optional.</param>
    public SettingsService(ISettingsStore store, AppSettings initial, bool persistenceSuspended = false, ILogger? logger = null)
    {
        _store = store;
        _logger = logger;
        IsPersistenceSuspended = persistenceSuspended;
        Current = initial.Sanitized();
    }

    /// <summary>
    /// Loads the settings for startup and builds the service. Never throws: a store that throws, or one that reports it could not read
    /// the file, yields the defaults AND a service that does not write them over the user's file (see the class remarks).
    /// </summary>
    internal static SettingsService LoadFrom(ISettingsStore store, ILogger logger)
    {
        var threw = false;
        var settings = StartupGuard.Run(
            logger,
            "load settings",
            () =>
            {
                try
                {
                    return store.Load();
                }
                catch
                {
                    threw = true;
                    throw; // StartupGuard logs it by step name
                }
            },
            AppSettings.Default);

        return new SettingsService(store, settings, persistenceSuspended: threw || store.LoadFailedToRead, logger);
    }

    /// <summary>True when changes are kept for this session only because the settings file could not be read at startup.</summary>
    public bool IsPersistenceSuspended { get; }

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
        if (IsPersistenceSuspended)
        {
            if (_logger is not null)
            {
                AppLog.SettingsWriteSuspended(_logger);
            }
        }
        else
        {
            _store.TrySave(next);
        }

        Changed?.Invoke(this, new SettingsChangedEventArgs(previous, next));
    }
}
