using System.Collections;
using System.Globalization;
using System.Resources;
using ForzaTelemetry.App.Resources;

namespace ForzaTelemetry.App.Tests;

public class LocalizationTests
{
    [Fact]
    public void Every_neutral_string_has_a_non_empty_Spanish_translation()
    {
        var spanish = CultureInfo.GetCultureInfo("es");
        var neutral = Strings.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)!;
        // tryParents: false = only strings defined IN the Spanish file count, not ones that silently fall back to English.
        var translated = Strings.ResourceManager.GetResourceSet(spanish, createIfNotExists: true, tryParents: false)!;

        var missing = new List<string>();
        foreach (DictionaryEntry entry in neutral)
        {
            var key = (string)entry.Key;
            if (translated.GetString(key) is not { Length: > 0 })
            {
                missing.Add(key);
            }
        }

        Assert.True(missing.Count == 0, "Missing Spanish strings: " + string.Join(", ", missing));
    }

    [Fact]
    public void Spanish_does_not_define_strings_that_the_neutral_file_lacks()
    {
        var spanish = CultureInfo.GetCultureInfo("es");
        var neutral = Strings.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var translated = Strings.ResourceManager.GetResourceSet(spanish, true, false)!;

        foreach (DictionaryEntry entry in translated)
        {
            Assert.NotNull(neutral.GetString((string)entry.Key));
        }
    }

    [Fact]
    public void Format_placeholders_match_between_English_and_Spanish()
    {
        var spanish = CultureInfo.GetCultureInfo("es");
        var neutral = Strings.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var translated = Strings.ResourceManager.GetResourceSet(spanish, true, false)!;

        foreach (DictionaryEntry entry in neutral)
        {
            var key = (string)entry.Key;
            Assert.True(
                Placeholders((string)entry.Value!).SequenceEqual(Placeholders(translated.GetString(key)!)),
                $"Placeholders of '{key}' differ between languages.");
        }
    }

    private static IEnumerable<string> Placeholders(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"\{(\d+)").Select(m => m.Groups[1].Value).Order();
}
