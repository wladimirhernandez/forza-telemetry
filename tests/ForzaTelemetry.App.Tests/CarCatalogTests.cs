using System.IO;
using System.Text;
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

    private CarNameStore Store(CarCatalog? catalog) => new(Path.Combine(_directory, "cars.json"), catalog: catalog);

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
        var store = Store(From("""{ "cars": { "3983": "2024 BMW X6 M" } }"""));

        Assert.Equal("2024 BMW X6 M", CarDisplayName.For(3983, store));
        Assert.Equal(CarDisplayName.Unnamed(77), CarDisplayName.For(77, store));

        store.TrySet(3983, "School run");
        Assert.Equal("School run", CarDisplayName.For(3983, store));
        Assert.Equal("2024 BMW X6 M", CarDisplayName.Default(3983, store)); // what "Use the default name" returns to

        store.Remove(3983);
        Assert.Equal("2024 BMW X6 M", CarDisplayName.For(3983, store));
    }

    [Fact]
    public void Without_a_catalog_the_store_falls_back_to_the_number()
    {
        var store = Store(null);

        Assert.Null(store.CatalogName(3983));
        Assert.Equal(CarDisplayName.Unnamed(3983), CarDisplayName.For(3983, store));
        Assert.Equal(CarDisplayName.Unnamed(3983), CarDisplayName.For(3983, null));
    }

    [Fact]
    public void The_rename_box_shows_the_catalog_name_as_the_default_and_use_default_restores_it()
    {
        var store = Store(From("""{ "cars": { "3983": "2024 BMW X6 M" } }"""));
        var box = new CarRenameViewModel(store);

        box.Begin(3983);
        Assert.True(box.HasDefaultName);
        Assert.Contains("2024 BMW X6 M", box.DefaultNameText);
        Assert.Equal("", box.Text); // no nickname: the box is empty, the default is a hint

        box.Text = "School run";
        box.SaveCommand.Execute(null);
        Assert.Equal("School run", CarDisplayName.For(3983, store));

        box.Begin(3983);
        box.UseDefaultCommand.Execute(null);
        Assert.Equal("2024 BMW X6 M", CarDisplayName.For(3983, store));

        box.Begin(77); // a car the list does not know: no default line
        Assert.False(box.HasDefaultName);
    }
}
