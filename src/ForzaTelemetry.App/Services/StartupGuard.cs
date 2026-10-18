using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.App.Services;

/// <summary>
/// Runs one optional step of the startup composition so that a failure costs that step only, never the app.
/// </summary>
/// <remarks>
/// <b>Why a guard and not a try/catch around the whole composition root.</b> <c>App.OnStartup</c> already has one, and it shows
/// the "Startup error" dialog and exits: right for a failure with no way forward (the window cannot be built), wrong for
/// a settings folder that is read-only, a history file with an odd name or a culture the runtime does not know. Those steps
/// have a perfectly good default (factory settings, an empty history, English), so each one is wrapped here: the failure is
/// logged with the step's name and the app opens anyway. Only what is NOT wrapped can still reach the startup dialog, which
/// keeps that dialog meaning "this really cannot start".
/// <para>
/// It catches every <see cref="Exception"/> on purpose: the steps touch the file system, the registry-backed folder APIs and
/// platform globalization, and the set of things they can throw is not something to enumerate (security, path length,
/// unsupported path formats, sharing violations...). Nothing here is retried: a step that failed once is done for this run.
/// </para>
/// </remarks>
internal static class StartupGuard
{
    /// <summary>Runs <paramref name="action"/>; on failure logs a warning naming <paramref name="step"/> and returns false.</summary>
    public static bool Run(ILogger logger, string step, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.StartupStepFailed(logger, ex, step);
            return false;
        }
    }

    /// <summary>Runs <paramref name="action"/>; on failure logs a warning naming <paramref name="step"/> and returns <paramref name="fallback"/>.</summary>
    public static T Run<T>(ILogger logger, string step, Func<T> action, T fallback)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            AppLog.StartupStepFailed(logger, ex, step);
            return fallback;
        }
    }
}
