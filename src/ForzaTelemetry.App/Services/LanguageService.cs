using System.Globalization;

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
    /// <summary>
    /// The UI culture Windows gave this process, captured the first time this class is touched. That is in
    /// <c>OnStartup</c> BEFORE <see cref="Apply"/> overrides the thread's culture, which is what makes it the answer to
    /// "what would System mean?" for the rest of the run (afterwards <see cref="CultureInfo.CurrentUICulture"/> reports the
    /// forced language and the original is gone).
    /// </summary>
    public static CultureInfo SystemCulture { get; } = CultureInfo.CurrentUICulture;

    /// <summary>
    /// The culture to force for <paramref name="language"/>, or null for "System": leave the process on whatever Windows
    /// provides.
    /// </summary>
    public static CultureInfo? ResolveCulture(AppLanguage language) => language switch
    {
        AppLanguage.English => CultureInfo.GetCultureInfo("en"),
        AppLanguage.Spanish => CultureInfo.GetCultureInfo("es"),
        _ => null
    };

    /// <summary>
    /// Sets the UI culture for the current thread (the UI thread, which is where the XAML is loaded) and as the default
    /// for threads created afterwards. Returns the culture now in effect.
    /// </summary>
    public static CultureInfo Apply(AppLanguage language)
    {
        if (ResolveCulture(language) is { } culture)
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
