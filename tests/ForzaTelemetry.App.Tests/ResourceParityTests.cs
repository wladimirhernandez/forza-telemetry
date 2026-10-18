using System.Collections;
using System.Globalization;
using System.Resources;
using ForzaTelemetry.App.Resources;

namespace ForzaTelemetry.App.Tests;

/// <summary>
/// Every resource file added after the main <c>Strings</c> one (the connection guide, the car rename box) needs its Spanish twin: the same
/// keys, a non-empty translation for each, and the same <c>{0}</c> placeholders. (<c>LocalizationTests</c> does this for <c>Strings</c>.)
/// </summary>
public class ResourceParityTests
{
    public static TheoryData<string> Sets => new() { nameof(SetupStrings), nameof(CarNameStrings) };

    private static ResourceManager ManagerOf(string name) => name switch
    {
        nameof(SetupStrings) => SetupStrings.ResourceManager,
        nameof(CarNameStrings) => CarNameStrings.ResourceManager,
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    [Theory]
    [MemberData(nameof(Sets))]
    public void Every_string_has_a_Spanish_translation_with_the_same_placeholders(string set)
    {
        var manager = ManagerOf(set);
        var neutral = manager.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var translated = manager.GetResourceSet(CultureInfo.GetCultureInfo("es"), true, false)!;

        foreach (DictionaryEntry entry in neutral)
        {
            var key = (string)entry.Key;
            var es = translated.GetString(key);
            Assert.False(string.IsNullOrEmpty(es), $"{set}: missing Spanish string '{key}'.");
            Assert.True(
                Placeholders((string)entry.Value!).SequenceEqual(Placeholders(es!)),
                $"{set}: placeholders of '{key}' differ between languages.");
        }

        foreach (DictionaryEntry entry in translated)
        {
            Assert.True(neutral.GetString((string)entry.Key) is not null, $"{set}: Spanish defines '{entry.Key}', which the neutral file lacks.");
        }
    }

    [Theory]
    [MemberData(nameof(Sets))]
    public void The_accessor_has_a_property_for_every_key(string set)
    {
        var accessor = typeof(SetupStrings).Assembly.GetType("ForzaTelemetry.App.Resources." + set)!;
        var neutral = ManagerOf(set).GetResourceSet(CultureInfo.InvariantCulture, true, false)!;

        foreach (DictionaryEntry entry in neutral)
        {
            Assert.NotNull(accessor.GetProperty((string)entry.Key));
        }
    }

    private static IEnumerable<string> Placeholders(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"\{(\d+)").Select(m => m.Groups[1].Value).Order();
}
