using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForzaTelemetry.App.Services;

/// <summary>
/// The real names of the game's cars, from a community-maintained list bundled with the app (<c>CarCatalog.fh6.json</c>, credited in
/// THIRD-PARTY-NOTICES.txt). Read-only: the player's own nicknames live in <see cref="CarNameStore"/> and always win over this list.
/// </summary>
/// <remarks>
/// <b>Tolerant by design.</b> The list is a convenience, never a requirement: a missing or damaged resource gives an EMPTY catalog (cars then
/// show as "Car #1234") and a log line, and one bad entry costs only itself. The file is small (about 26 KB), so it is read once at startup.
/// </remarks>
public sealed class CarCatalog
{
    /// <summary>The longest catalog name accepted, in characters (longer than the 40 a player may type).</summary>
    public const int MaxNameLength = 80;

    /// <summary>The name of the embedded resource.</summary>
    public const string ResourceName = "ForzaTelemetry.App.Resources.CarCatalog.fh6.json";

    private readonly Dictionary<int, string> _names;

    private CarCatalog(Dictionary<int, string> names) => _names = names;

    /// <summary>A catalog with no cars (what a missing or unreadable list becomes).</summary>
    public static CarCatalog Empty { get; } = new([]);

    /// <summary>How many cars the catalog knows.</summary>
    public int Count => _names.Count;

    /// <summary>The real name of a car model, or false when the list does not know it.</summary>
    public bool TryGetName(int carOrdinal, out string name)
    {
        if (_names.TryGetValue(carOrdinal, out var found))
        {
            name = found;
            return true;
        }

        name = "";
        return false;
    }

    /// <summary>Reads the list embedded in this assembly. Never throws: a missing or damaged resource gives <see cref="Empty"/>.</summary>
    public static CarCatalog LoadEmbedded(ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                AppLog.CarCatalogMissing(logger, ResourceName);
                return Empty;
            }

            return Load(stream, logger);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            AppLog.CarCatalogUnreadable(logger, ex);
            return Empty;
        }
    }

    /// <summary>Parses <c>{ "cars": { "3983": "2024 BMW X6 M", ... } }</c>. Entries that are not a positive ordinal with a clean name are skipped.</summary>
    public static CarCatalog Load(Stream stream, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        try
        {
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("cars", out var cars)
                || cars.ValueKind != JsonValueKind.Object)
            {
                AppLog.CarCatalogUnreadable(logger, new JsonException("No 'cars' object."));
                return Empty;
            }

            var names = new Dictionary<int, string>();
            var skipped = 0;
            foreach (var entry in cars.EnumerateObject())
            {
                if (int.TryParse(entry.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal)
                    && ordinal > 0
                    && entry.Value.ValueKind == JsonValueKind.String
                    && Clean(entry.Value.GetString()) is { } name)
                {
                    names.TryAdd(ordinal, name); // the first of a duplicate wins
                }
                else
                {
                    skipped++;
                }
            }

            if (skipped > 0)
            {
                AppLog.CarCatalogEntriesSkipped(logger, skipped);
            }

            AppLog.CarCatalogLoaded(logger, names.Count);
            return new CarCatalog(names);
        }
        catch (JsonException ex)
        {
            AppLog.CarCatalogUnreadable(logger, ex);
            return Empty;
        }
    }

    // A usable name is non-empty, already trimmed, short and free of control characters; anything else is skipped, not repaired.
    private static string? Clean(string? raw) =>
        raw is { Length: > 0 and <= MaxNameLength } && raw == raw.Trim() && !raw.Any(char.IsControl) ? raw : null;
}
