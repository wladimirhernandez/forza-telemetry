using System.Globalization;
using System.IO;
using System.Text;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;

namespace ForzaTelemetry.App.Tests;

/// <summary>The bundled real car names: the shipped list, tolerant loading, and the nickname > catalog > number rule.</summary>
public sealed class CarCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ForzaTelemetryTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static CarCatalog From(string json) => CarCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private CarNameStore Store() => new(Path.Combine(_directory, "cars.json"));

    [Fact]
    public void The_shipped_list_loads_and_knows_the_car_the_player_checked_in_game()
    {
        var catalog = CarCatalog.LoadEmbedded();

        Assert.True(catalog.Count > 500);
        Assert.True(catalog.TryGetName(3983, out var name));
        Assert.Equal("2024 BMW X6 M", name);
        Assert.True(catalog.TryGetName(247, out var first));
        Assert.Equal("1969 Toyota 2000 GT", first);
        Assert.False(catalog.TryGetName(1, out _));
    }

    [Fact]
    public void Every_shipped_name_is_clean()
    {
        // The loader skips bad entries silently; this proves the SHIPPED file has none to skip.
        using var stream = typeof(CarCatalog).Assembly.GetManifestResourceStream(CarCatalog.ResourceName)!;
        using var document = System.Text.Json.JsonDocument.Parse(stream);
        var cars = document.RootElement.GetProperty("cars");

        var count = 0;
        foreach (var entry in cars.EnumerateObject())
        {
            count++;
            var name = entry.Value.GetString()!;
            Assert.True(int.Parse(entry.Name, System.Globalization.CultureInfo.InvariantCulture) > 0);
            Assert.NotEmpty(name);
            Assert.Equal(name.Trim(), name);
            Assert.InRange(name.Length, 1, CarCatalog.MaxNameLength);
            Assert.DoesNotContain(name, char.IsControl);
        }

        Assert.Equal(CarCatalog.LoadEmbedded().Count, count);
    }

    [Fact]
    public void Bad_entries_are_skipped_and_good_ones_kept()
    {
        var catalog = From("""
            { "cars": { "10": "Good Car", "0": "Zero", "-5": "Negative", "abc": "Not a number", "11": "", "12": " padded ",
                        "13": 42, "14": "tab\there", "15": "Another" } }
            """);

        Assert.Equal(2, catalog.Count);
        Assert.True(catalog.TryGetName(10, out var good));
        Assert.Equal("Good Car", good);
        Assert.True(catalog.TryGetName(15, out _));
    }

    [Fact]
    public void A_name_over_the_limit_is_skipped()
    {
        var catalog = From("{ \"cars\": { \"1\": \"" + new string('x', CarCatalog.MaxNameLength + 1) + "\", \"2\": \"" + new string('y', CarCatalog.MaxNameLength) + "\" } }");

        Assert.Equal(1, catalog.Count);
        Assert.True(catalog.TryGetName(2, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{ \"cars\": 5 }")]
    [InlineData("{ \"other\": {} }")]
    public void A_damaged_list_is_an_empty_catalog_and_never_throws(string json)
    {
        var catalog = From(json);

        Assert.Equal(0, catalog.Count);
        Assert.False(catalog.TryGetName(3983, out _));
    }

    // ---- Resolution order -------------------------------------------------------------------------------------

    [Fact]
    public void The_display_name_is_the_nickname_then_the_catalog_name_then_the_number()
    {
        var store = Store();
        var names = new CarNames(From("""{ "cars": { "3983": "2024 BMW X6 M" } }"""), store);

        Assert.Equal("2024 BMW X6 M", CarDisplayName.For(3983, names));
        Assert.Equal(CarDisplayName.Unnamed(77), CarDisplayName.For(77, names));

        store.TrySet(3983, "School run");
        Assert.Equal("School run", CarDisplayName.For(3983, names));
        Assert.Equal("School run", names.RealName(3983));
        Assert.Equal("2024 BMW X6 M", names.CatalogName(3983)); // what the rename box's default hint shows

        store.Remove(3983);
        Assert.Equal("2024 BMW X6 M", CarDisplayName.For(3983, names));
    }

    [Fact]
    public void The_real_name_is_never_the_placeholder_and_the_display_name_always_is_a_fallback()
    {
        var names = new CarNames(From("""{ "cars": { "3983": "2024 BMW X6 M" } }"""), Store());

        Assert.Null(names.RealName(77));
        Assert.Null(names.CatalogName(77));
        Assert.Equal(CarDisplayName.Unnamed(77), names.Display(77));
    }

    [Fact]
    public void Without_a_store_the_catalog_names_still_show()
    {
        var names = new CarNames(From("""{ "cars": { "3983": "2024 BMW X6 M" } }"""));

        Assert.Equal("2024 BMW X6 M", CarDisplayName.For(3983, names));
        Assert.Null(names.Nickname(3983));
    }

    [Fact]
    public void Without_a_catalog_or_a_store_the_number_is_all_there_is()
    {
        var names = new CarNames();

        Assert.Null(names.RealName(3983));
        Assert.Equal(CarDisplayName.Unnamed(3983), CarDisplayName.For(3983, names));
        Assert.Equal(CarDisplayName.Unnamed(3983), CarDisplayName.For(3983, null));
    }

    [Fact]
    public void The_resolver_forwards_nickname_changes()
    {
        var store = Store();
        var names = new CarNames(store: store);
        var raised = 0;
        names.Changed += (_, _) => raised++;

        store.TrySet(5, "Daily GT");

        Assert.Equal(1, raised);
    }

    // ---- The rename box -------------------------------------------------------------------------------------------

    [Fact]
    public void The_rename_box_titles_with_the_display_name_and_hints_the_catalog_name_as_the_default()
    {
        var store = Store();
        var names = new CarNames(From("""{ "cars": { "3983": "2024 BMW X6 M" } }"""), store);
        var box = new CarRenameViewModel(store, names);

        box.Begin(3983);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, CarNameStrings.CarRenameTitle, "2024 BMW X6 M"), box.Title);
        Assert.True(box.HasDefaultName);
        Assert.Contains("2024 BMW X6 M", box.DefaultNameText);
        Assert.False(box.HasNickname); // no nickname: no "use the default" action, and the box is empty
        Assert.Equal("", box.Text);
        Assert.Equal(box.DefaultNameText, box.HelpText); // the hint reaches the text box's accessible help

        box.Text = "School run";
        box.SaveCommand.Execute(null);
        Assert.Equal("School run", CarDisplayName.For(3983, names));

        box.Begin(3983);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, CarNameStrings.CarRenameTitle, "School run"), box.Title);
        Assert.True(box.HasNickname);
        box.UseDefaultCommand.Execute(null);
        Assert.Equal("2024 BMW X6 M", CarDisplayName.For(3983, names));
    }

    [Fact]
    public void The_rename_box_help_text_is_the_error_while_there_is_one_and_the_hint_otherwise()
    {
        var store = Store();
        var box = new CarRenameViewModel(store, new CarNames(From("""{ "cars": { "3983": "2024 BMW X6 M" } }"""), store));
        box.Begin(3983);

        box.Text = new string('x', CarNameStore.MaxLength + 1);
        Assert.Equal(box.Error, box.HelpText);
        Assert.NotNull(box.HelpText);

        box.Text = "ok";
        Assert.Equal(box.DefaultNameText, box.HelpText);
    }

    [Fact]
    public void The_rename_box_has_no_default_line_for_a_car_the_list_does_not_know()
    {
        var store = Store();
        var box = new CarRenameViewModel(store, new CarNames(From("""{ "cars": { "3983": "2024 BMW X6 M" } }"""), store));

        box.Begin(77);

        Assert.False(box.HasDefaultName);
        Assert.Null(box.HelpText);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, CarNameStrings.CarRenameTitle, CarDisplayName.Unnamed(77)), box.Title);
    }
}
