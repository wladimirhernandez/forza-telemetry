using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.Core.Insights;

namespace ForzaTelemetry.App.Tests;

/// <summary>Advice key to localized sentence mapping, and the integrity of the InsightsStrings resource set.</summary>
public class InsightTextTests
{
    [Theory]
    [InlineData(BalanceState.Understeer)]
    [InlineData(BalanceState.Oversteer)]
    [InlineData(BalanceState.Neutral)]
    [InlineData(BalanceState.Unknown)]
    public void Every_balance_state_has_advice(BalanceState state) =>
        Assert.False(string.IsNullOrWhiteSpace(InsightText.BalanceAdvice(state)));

    [Fact]
    public void Balance_labels_are_distinct_and_unknown_has_none()
    {
        Assert.Equal(InsightsStrings.BalanceUndersteer, InsightText.BalanceLabel(BalanceState.Understeer));
        Assert.Equal(InsightsStrings.BalanceOversteer, InsightText.BalanceLabel(BalanceState.Oversteer));
        Assert.Equal(InsightsStrings.BalanceNeutral, InsightText.BalanceLabel(BalanceState.Neutral));
        Assert.Equal(string.Empty, InsightText.BalanceLabel(BalanceState.Unknown));
    }

    [Theory]
    [InlineData(0.9, BalanceState.Understeer)]
    [InlineData(0.149, BalanceState.Neutral)]
    [InlineData(0, BalanceState.Neutral)]
    [InlineData(-0.149, BalanceState.Neutral)]
    public void ClassifyBalance_uses_the_analyzer_threshold(double index, BalanceState expected) =>
        Assert.Equal(expected, InsightText.ClassifyBalance(index));

    [Fact]
    public void ClassifyBalance_switches_exactly_at_the_analyzer_threshold()
    {
        // The threshold is a float constant; compare in the same precision so the boundary is exact.
        double threshold = BalanceAnalyzer.BalanceThreshold;

        Assert.Equal(BalanceState.Understeer, InsightText.ClassifyBalance(threshold));
        Assert.Equal(BalanceState.Oversteer, InsightText.ClassifyBalance(-threshold));
    }

    [Theory]
    [InlineData(TireAdvice.FrontHotterThanRear)]
    [InlineData(TireAdvice.RearHotterThanFront)]
    [InlineData(TireAdvice.LeftHotterThanRight)]
    [InlineData(TireAdvice.RightHotterThanLeft)]
    [InlineData(TireAdvice.AllTooCold)]
    [InlineData(TireAdvice.AllTooHot)]
    public void Each_tire_flag_maps_to_exactly_one_sentence(TireAdvice flag)
    {
        var lines = InsightText.TireLines(flag);

        Assert.Single(lines);
        Assert.False(string.IsNullOrWhiteSpace(lines[0]));
        Assert.NotEqual(InsightsStrings.TiresAdviceBalanced, lines[0]);
    }

    [Fact]
    public void Tire_flags_map_to_distinct_sentences()
    {
        var all = new[]
        {
            TireAdvice.FrontHotterThanRear, TireAdvice.RearHotterThanFront, TireAdvice.LeftHotterThanRight,
            TireAdvice.RightHotterThanLeft, TireAdvice.AllTooCold, TireAdvice.AllTooHot,
        }.Select(flag => InsightText.TireLines(flag)[0]).ToList();

        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void Several_tire_flags_give_several_sentences_in_a_stable_order()
    {
        var lines = InsightText.TireLines(TireAdvice.RightHotterThanLeft | TireAdvice.FrontHotterThanRear);

        Assert.Equal([InsightsStrings.TiresAdviceFrontHotter, InsightsStrings.TiresAdviceRightHotter], lines);
    }

    [Fact]
    public void No_tire_flags_means_balanced() =>
        Assert.Equal([InsightsStrings.TiresAdviceBalanced], InsightText.TireLines(TireAdvice.None));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(40, 2)]
    public void BottomingSeverity_grades_the_counter(int count, int expected) =>
        Assert.Equal(expected, InsightText.BottomingSeverity(count));

    [Fact]
    public void Suspension_advice_names_the_axle_that_bottoms_out()
    {
        Assert.Equal(InsightsStrings.SuspensionAdviceNone, InsightText.SuspensionAdvice(default));
        Assert.Equal(InsightsStrings.SuspensionAdviceFew, InsightText.SuspensionAdvice(new BottomingCounts(1, 0, 0, 1)));
        Assert.Equal(InsightsStrings.SuspensionAdviceFront, InsightText.SuspensionAdvice(new BottomingCounts(2, 2, 0, 0)));
        Assert.Equal(InsightsStrings.SuspensionAdviceRear, InsightText.SuspensionAdvice(new BottomingCounts(0, 0, 1, 4)));
        Assert.Equal(InsightsStrings.SuspensionAdviceBoth, InsightText.SuspensionAdvice(new BottomingCounts(3, 0, 0, 3)));
    }

    // ---- InsightsStrings resource integrity (same guarantees as LocalizationTests gives Strings) ----

    private static HashSet<string> Keys(CultureInfo culture, bool tryParents = false) =>
        InsightsStrings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents)!
            .Cast<DictionaryEntry>().Select(e => (string)e.Key).ToHashSet();

    [Fact]
    public void Every_insights_string_has_a_non_empty_Spanish_translation()
    {
        var spanish = CultureInfo.GetCultureInfo("es");
        var missing = Keys(CultureInfo.InvariantCulture)
            .Where(key => string.IsNullOrWhiteSpace(InsightsStrings.ResourceManager.GetResourceSet(spanish, true, false)!.GetString(key)))
            .ToList();

        Assert.True(missing.Count == 0, "Missing Spanish strings: " + string.Join(", ", missing));
    }

    [Fact]
    public void Spanish_defines_no_insights_string_the_neutral_file_lacks() =>
        Assert.Empty(Keys(CultureInfo.GetCultureInfo("es")).Except(Keys(CultureInfo.InvariantCulture)));

    [Fact]
    public void Insights_format_placeholders_match_between_English_and_Spanish()
    {
        var spanish = InsightsStrings.ResourceManager.GetResourceSet(CultureInfo.GetCultureInfo("es"), true, false)!;
        foreach (var key in Keys(CultureInfo.InvariantCulture))
        {
            var english = InsightsStrings.ResourceManager.GetString(key, CultureInfo.InvariantCulture)!;
            Assert.True(
                Placeholders(english).SequenceEqual(Placeholders(spanish.GetString(key)!)),
                $"Placeholders of '{key}' differ between languages.");
        }
    }

    [Fact]
    public void The_hand_written_accessor_has_a_property_for_every_key()
    {
        var properties = typeof(InsightsStrings)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Empty(Keys(CultureInfo.InvariantCulture).Except(properties));
        Assert.Empty(properties.Except(Keys(CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Every_accessor_returns_text()
    {
        foreach (var property in typeof(InsightsStrings).GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(string)))
        {
            Assert.False(string.IsNullOrEmpty((string?)property.GetValue(null)), property.Name);
        }
    }

    private static IEnumerable<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{\d+[^}]*\}").Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal);
}
