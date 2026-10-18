namespace ForzaTelemetry.App.Services;

/// <summary>
/// The one read-only answer to "what is this car called?": the player's nickname (<see cref="CarNameStore"/>) over the bundled real names
/// (<see cref="CarCatalog"/>). Screens take this, never the mutable store, so they cannot change names and cannot forget the catalog.
/// </summary>
/// <remarks>Either source may be missing (no store in tests, no catalog when the list could not be read); the answer then falls back to
/// what is left. <see cref="Display"/> ends with the localized "Car #1234"; <see cref="RealName"/> never does.</remarks>
public sealed class CarNames
{
    private readonly CarCatalog _catalog;
    private readonly CarNameStore? _store;

    /// <param name="catalog">The bundled real names; optional (none: only nicknames and "Car #1234").</param>
    /// <param name="store">The player's nicknames; optional (none: only real names and "Car #1234").</param>
    public CarNames(CarCatalog? catalog = null, CarNameStore? store = null)
    {
        _catalog = catalog ?? CarCatalog.Empty;
        _store = store;
    }

    /// <summary>Raised when a nickname was set, changed or removed.</summary>
    public event EventHandler? Changed
    {
        add
        {
            if (_store is not null)
            {
                _store.Changed += value;
            }
        }
        remove
        {
            if (_store is not null)
            {
                _store.Changed -= value;
            }
        }
    }

    /// <summary>The nickname of a car model, or null when the player never named it.</summary>
    public string? Nickname(int carOrdinal) => _store?.Find(carOrdinal);

    /// <summary>The real name from the bundled list, or null when it does not know the car. Ignores nicknames.</summary>
    public string? CatalogName(int carOrdinal) => _catalog.TryGetName(carOrdinal, out var name) ? name : null;

    /// <summary>The nickname, else the catalog name, else null: a name that means something, never a placeholder.</summary>
    public string? RealName(int carOrdinal) => Nickname(carOrdinal) ?? CatalogName(carOrdinal);

    /// <summary>What the car is called on screen: <see cref="RealName"/>, else the localized "Car #1234".</summary>
    public string Display(int carOrdinal) => RealName(carOrdinal) ?? CarDisplayName.Unnamed(carOrdinal);
}
