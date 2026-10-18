using System.Globalization;
using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.App.Services;

/// <summary>Applies the user's language choice to the process before any UI text is read.</summary>
/// <remarks>
/// WPF text comes from <c>Strings.resx</c> through a <c>ResourceManager</c>, which picks the satellite resource (en, es) by
/// <see cref="CultureInfo.CurrentUICulture"/> of the thread that asks. So "changing the language" is nothing more than
/// setting that culture BEFORE the first string is read (the XAML <c>{x:Static}</c> values are read once, when the window
/// loads). That is why a language change needs a restart: texts already on screen were looked up under the old culture.
/// <para>
/// Only the UI culture is changed, not <see cref="CultureInfo.CurrentCulture"/> (the "regional format"): Windows keeps
/// "display language" and "regional format" separate, and a user who reads the UI in English may still want
/// their own number formatting.
/// </para>
/// </remarks>
public static class LanguageService
{
    // The culture Windows gave this process, captured ONCE (first call wins). See CaptureSystemCulture for why this is a
    // method that must run before Apply, and not a static initializer.
    private static CultureInfo? _systemCulture;

    /// <summary>
    /// The UI culture Windows gave this process: what "System" means for the rest of the run (afterwards
    /// <see cref="CultureInfo.CurrentUICulture"/> reports the forced language and the original is gone).
    /// </summary>
    public static CultureInfo SystemCulture => _systemCulture ?? CaptureSystemCulture();

    /// <summary>
    /// True when the runtime runs without culture data (<c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT</c> or the runtimeconfig switch).
    /// The app survives that for the language choice (see <see cref="ResolveCulture(AppLanguage, ILogger?)"/>), but WPF itself cannot
    /// draw text there, so the startup dialog uses this to explain the cause.
    /// </summary>
    /// <remarks>
    /// Three signals, because each misses a case the others catch: the runtimeconfig switch (what a published app carries), the
    /// environment variable (what a hardened image sets, and which the switch lookup does not report), and a probe of a culture every
    /// Windows machine has, which fails whenever the runtime has no culture data for ANY reason (a missing ICU/NLS included).
    /// </remarks>
    public static bool IsInvariantGlobalization =>
        DetectInvariantGlobalization(
            AppContext.TryGetSwitch("System.Globalization.Invariant", out var invariant) ? invariant : null,
            Environment.GetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"),
            CultureInfo.GetCultureInfo);

    /// <summary>The detection with its three inputs passed in, so a test can supply each one.</summary>
    /// <param name="switchValue">The runtimeconfig/AppContext switch, or null when it is not set.</param>
    /// <param name="environmentValue">The raw <c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT</c> value; "true" (any casing) and "1" mean on. Not consulted when the switch is explicitly false.</param>
    /// <param name="lookup">The culture lookup the probe asks for a culture that always exists when culture data does.</param>
    internal static bool DetectInvariantGlobalization(bool? switchValue, string? environmentValue, Func<string, CultureInfo> lookup)
    {
        if (switchValue is true)
        {
            return true;
        }

        // An explicit "false" in runtimeconfig wins over the environment: the runtime reads the switch first and only falls back to the
        // variable when the switch is absent, so a false switch means culture data IS in use whatever the variable says.
        var text = switchValue is null ? environmentValue?.Trim() : null;
        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1")
        {
            return true;
        }

        try
        {
            lookup("en-US");
            return false;
        }
        catch (Exception)
        {
            // Any failure of the probe means the same thing for the caller: no usable culture data. (Not only
            // CultureNotFoundException: this is platform code, see ResolveCulture.)
            return true;
        }
    }

    /// <summary>
    /// Remembers the culture Windows gave this process. Idempotent: only the FIRST call records anything, so a later call
    /// (after <see cref="Apply"/> changed the thread's culture) can never overwrite the original.
    /// <para>
    /// <b>Why an explicit call and not a static property initializer.</b> That is what this used to be, and it was wrong in a
    /// subtle way: a C# static initializer runs lazily, when the type is first TOUCHED, and the first member that touched it
    /// was often <see cref="Apply"/> itself, whose first statement overrides the culture. Whether the capture ran before or
    /// after that override depended on how the JIT ordered things, so "System" could silently mean "the language I just
    /// forced". <c>App.OnStartup</c> now calls this method first, and <see cref="Apply"/> calls it too as a belt, so the order
    /// no longer depends on anyone remembering.
    /// </para>
    /// </summary>
    public static CultureInfo CaptureSystemCulture() => CaptureOnce(ref _systemCulture, CultureInfo.CurrentUICulture);

    /// <summary>The "first call wins" rule on its own, so it can be tested without touching the process-wide slot.</summary>
    internal static CultureInfo CaptureOnce(ref CultureInfo? slot, CultureInfo current) => slot ??= current;

    /// <summary>
    /// The culture to force for <paramref name="language"/>, or null when nothing is forced: "System" (leave the process on
    /// whatever Windows provides), or a language whose culture cannot be loaded on this machine.
    /// </summary>
    /// <remarks>
    /// <b>Why this never throws.</b> <c>CultureInfo.GetCultureInfo("es")</c> throws <see cref="CultureNotFoundException"/> when the
    /// runtime has no culture data: invariant-globalization mode (<c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT</c>, set by some
    /// hardened or minimal images) and a missing ICU/NLS both do it. This runs in the composition root, before the main window and
    /// its error handling exist, so a throw here used to end the app with "Startup failed" over a cosmetic choice. The
    /// fallback is the culture the process already has (in those environments the invariant one), whose resource lookups land
    /// on the neutral English strings, so every screen still has text.
    /// </remarks>
    /// <param name="language">The user's choice.</param>
    /// <param name="logger">Receives a warning when the culture is unavailable; optional.</param>
    public static CultureInfo? ResolveCulture(AppLanguage language, ILogger? logger = null) =>
        ResolveCulture(language, CultureInfo.GetCultureInfo, logger);

    /// <summary>The same, with the culture lookup passed in so a test can make it fail like invariant-globalization mode does.</summary>
    internal static CultureInfo? ResolveCulture(AppLanguage language, Func<string, CultureInfo> lookup, ILogger? logger)
    {
        var name = language switch
        {
            AppLanguage.English => "en",
            AppLanguage.Spanish => "es",
            _ => null
        };

        if (name is null)
        {
            return null;
        }

        try
        {
            return lookup(name);
        }
        catch (Exception ex)
        {
            // Every exception, not only CultureNotFoundException: the lookup is platform code (ICU/NLS, globalization
            // switches), and the cost of a surprise here is the whole app failing to open. The log keeps the evidence.
            if (logger is not null)
            {
                AppLog.CultureUnavailable(logger, ex, name);
            }

            return null;
        }
    }

    /// <summary>
    /// Sets the UI culture for the current thread (the UI thread, which is where the XAML is loaded) and as the default
    /// for threads created afterwards. Returns the culture now in effect; when the chosen culture cannot be loaded, that is
    /// simply the one the process already had (see <see cref="ResolveCulture(AppLanguage, ILogger?)"/>).
    /// </summary>
    public static CultureInfo Apply(AppLanguage language, ILogger? logger = null) =>
        Apply(language, CultureInfo.GetCultureInfo, logger);

    internal static CultureInfo Apply(AppLanguage language, Func<string, CultureInfo> lookup, ILogger? logger)
    {
        CaptureSystemCulture(); // before anything below can change the culture (see that method)

        if (ResolveCulture(language, lookup, logger) is { } culture)
        {
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }

        return CultureInfo.CurrentUICulture;
    }

    /// <summary>
    /// The culture <paramref name="language"/> actually shows: the forced culture, or <paramref name="system"/> (default
    /// <see cref="SystemCulture"/>) for <see cref="AppLanguage.System"/>.
    /// </summary>
    public static CultureInfo Resolve(AppLanguage language, CultureInfo? system = null) =>
        ResolveCulture(language) ?? system ?? SystemCulture;

    /// <summary>
    /// True when the two choices put the SAME text on screen. Comparing the enum values would say "System" and "English"
    /// differ on a PC that is already in English, and offer a pointless restart; what matters is the culture each resolves
    /// to. Only the languages the app ships count: Windows in French or Swiss-German falls back to the neutral English
    /// resources, so it is the same display language as English.
    /// </summary>
    public static bool ResolvesToSameDisplayLanguage(AppLanguage first, AppLanguage second, CultureInfo? system = null) =>
        ShippedLanguage(Resolve(first, system)) == ShippedLanguage(Resolve(second, system));

    // The resource set a culture lands on: Spanish (es, es-MX, es-AR, ...) has its own file, everything else uses the neutral one.
    private static string ShippedLanguage(CultureInfo culture) =>
        string.Equals(culture.TwoLetterISOLanguageName, "es", StringComparison.OrdinalIgnoreCase) ? "es" : "en";
}
