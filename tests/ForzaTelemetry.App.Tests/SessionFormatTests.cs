using System.Collections;
using System.Globalization;
using System.Resources;
using ForzaTelemetry.App.Resources;

namespace ForzaTelemetry.App.Tests;

public class SessionFormatTests
{
    private static string Dec => CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(0.6, "0:00")] // rounded down: a clock must not run ahead of the recording
    [InlineData(59.99, "0:59")]
    [InlineData(60, "1:00")]
    [InlineData(754, "12:34")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3725, "1:02:05")]
    [InlineData(-5, "0:00")]
    public void Duration_reads_as_minutes_and_seconds_and_hours_only_from_one_hour(double seconds, string expected) =>
        Assert.Equal(expected, SessionFormat.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Size_picks_the_largest_sensible_unit_with_one_decimal_from_kilobytes_up()
    {
        Assert.Equal("0 B", SessionFormat.Size(0));
        Assert.Equal("812 B", SessionFormat.Size(812));
        Assert.Equal($"1{Dec}0 KB", SessionFormat.Size(1024));
        Assert.Equal($"41{Dec}2 KB", SessionFormat.Size((long)(41.2 * 1024)));
        Assert.Equal($"1{Dec}3 MB", SessionFormat.Size((long)(1.3 * 1024 * 1024)));
        Assert.Equal($"2{Dec}0 GB", SessionFormat.Size(2L * 1024 * 1024 * 1024));
        Assert.Equal("0 B", SessionFormat.Size(-1));
    }

    [Fact]
    public void Cars_name_the_first_and_count_the_rest_or_say_there_was_no_race()
    {
        Assert.Equal(SessionsStrings.SessionsNoCars, SessionFormat.Cars([]));
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.PerfCarOrdinalFormat, 1234), SessionFormat.Cars([1234]));
        Assert.Equal(
            string.Format(
                CultureInfo.CurrentCulture, SessionsStrings.SessionsCarsMoreFormat,
                string.Format(CultureInfo.CurrentCulture, Strings.PerfCarOrdinalFormat, 1234), 2),
            SessionFormat.Cars([1234, 77, 5]));
    }

    [Fact]
    public void The_replay_label_is_an_unambiguous_date_and_time_in_every_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");
            Assert.Equal("2026-10-18 01:23", SessionFormat.ReplayLabel(new DateTimeOffset(2026, 10, 18, 1, 23, 45, TimeSpan.Zero)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}

/// <summary>The Sessions strings obey the same translation rules as the main ones (the main set has its own test).</summary>
public class SessionsStringsLocalizationTests
{
    private static ResourceSet Set(CultureInfo culture) =>
        SessionsStrings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!;

    [Fact]
    public void Every_neutral_Sessions_string_has_a_Spanish_translation_and_no_extra_ones_exist()
    {
        var neutral = Set(CultureInfo.InvariantCulture);
        var spanish = Set(CultureInfo.GetCultureInfo("es"));

        var missing = neutral.Cast<DictionaryEntry>().Select(e => (string)e.Key).Where(k => spanish.GetString(k) is not { Length: > 0 }).ToList();
        var extra = spanish.Cast<DictionaryEntry>().Select(e => (string)e.Key).Where(k => neutral.GetString(k) is null).ToList();

        Assert.True(missing.Count == 0, "Missing Spanish strings: " + string.Join(", ", missing));
        Assert.True(extra.Count == 0, "Spanish strings without a neutral one: " + string.Join(", ", extra));
    }

    [Fact]
    public void Format_placeholders_match_between_the_languages()
    {
        var neutral = Set(CultureInfo.InvariantCulture);
        var spanish = Set(CultureInfo.GetCultureInfo("es"));

        foreach (DictionaryEntry entry in neutral)
        {
            Assert.True(
                Placeholders((string)entry.Value!).SequenceEqual(Placeholders(spanish.GetString((string)entry.Key)!)),
                $"Placeholders of '{entry.Key}' differ between languages.");
        }
    }

    [Fact]
    public void Every_property_of_the_accessor_has_a_resource_and_the_other_way_round()
    {
        var neutral = Set(CultureInfo.InvariantCulture).Cast<DictionaryEntry>().Select(e => (string)e.Key).Order().ToList();
        var properties = typeof(SessionsStrings).GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .Order()
            .ToList();

        Assert.Equal(neutral, properties);
    }

    private static IEnumerable<string> Placeholders(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"\{(\d+)").Select(m => m.Groups[1].Value).Order();
}
