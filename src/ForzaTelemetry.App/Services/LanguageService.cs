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
}
