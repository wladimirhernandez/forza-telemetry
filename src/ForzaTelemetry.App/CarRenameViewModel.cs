using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;

namespace ForzaTelemetry.App;

/// <summary>
/// The rename box: one instance shared by every place that offers "rename this car" (the top bar pill, Performance), shown as a small
/// card over the window while <see cref="IsOpen"/>.
/// </summary>
/// <remarks>
/// <b>Why one shared box.</b> The entry points are different buttons on different screens but the job is the same (type a name for car
/// number N), so there is one view model and one view, and an entry point only calls <see cref="Begin"/> with the car it means. Validation is a
/// computed property (<see cref="Error"/>) shown while typing, like the Settings port box; Enter saves, Esc cancels.
/// </remarks>
public sealed partial class CarRenameViewModel : ObservableObject
{
    private readonly CarNameStore _store;
    private readonly CarNames _names;

    /// <param name="store">Where the name is saved.</param>
    /// <param name="names">What cars are called now (nickname over catalog); optional, by default nicknames only.</param>
    public CarRenameViewModel(CarNameStore store, CarNames? names = null)
    {
        _store = store;
        _names = names ?? new CarNames(store: store);
    }

    /// <summary>The box is on screen.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>The car being named.</summary>
    public int CarOrdinal { get; private set; }

    /// <summary>What the box asks: "Name 2024 BMW X6 M" (the name the car has now, so it identifies the car; "Name Car #1234" when nothing knows it).</summary>
    [ObservableProperty]
    private string _title = "";

    /// <summary>The text in the box. Every change re-validates, so the message and the Save button follow the typing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(HelpText))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _text = "";

    /// <summary>True when the car already has a nickname, so "Use the default name" makes sense.</summary>
    [ObservableProperty]
    private bool _hasNickname;

    /// <summary>"If you leave this empty, it shows as 2024 BMW X6 M" when the bundled list knows the car, else empty (no line: the car then
    /// shows as "Car #1234", which says nothing worth explaining).</summary>
    [ObservableProperty]
    private string _defaultNameText = "";

    /// <summary>The default-name line is showing.</summary>
    public bool HasDefaultName => DefaultNameText.Length > 0;

    partial void OnDefaultNameTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasDefaultName));
        OnPropertyChanged(nameof(HelpText));
    }

    /// <summary>What a screen reader announces after the text box's name: the error when there is one, else the default-name hint (null when neither).</summary>
    public string? HelpText => Error ?? (HasDefaultName ? DefaultNameText : null);

    /// <summary>The localized reason the text cannot be saved, or null when it can.</summary>
    public string? Error => Problem == CarNameProblem.None ? null : CarNameStore.Describe(Problem);

    /// <summary>The message is showing.</summary>
    public bool HasError => Error is not null;

    // An EMPTY box right after opening is not an error to show yet (nothing was typed): the Save button is simply disabled. Once the
    // player has typed and emptied it, "Enter a name" is the helpful message.
    private bool _touched;

    private CarNameProblem Problem
    {
        get
        {
            var problem = CarNameStore.Validate(Text, out _);
            return problem == CarNameProblem.Empty && !_touched ? CarNameProblem.None : problem;
        }
    }

    /// <summary>Opens the box for a car, with its current nickname (if any) selected for editing.</summary>
    /// <param name="carOrdinal">The game's id of the car model; ignored when not positive (no car yet).</param>
    public void Begin(int carOrdinal)
    {
        if (carOrdinal <= 0)
        {
            return;
        }

        CarOrdinal = carOrdinal;
        Title = string.Format(CultureInfo.CurrentCulture, CarNameStrings.CarRenameTitle, _names.Display(carOrdinal));
        var existing = _names.Nickname(carOrdinal);
        HasNickname = existing is not null;
        DefaultNameText = _names.CatalogName(carOrdinal) is { } real
            ? string.Format(CultureInfo.CurrentCulture, CarNameStrings.CarRenameDefaultName, real)
            : "";
        Text = existing ?? "";
        _touched = false; // set after the text: assigning it counts as typing
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HelpText));
        SaveCommand.NotifyCanExecuteChanged();
        IsOpen = true;
    }

    partial void OnTextChanged(string value) => _touched = true;

    private bool CanSave() => CarNameStore.Validate(Text, out _) == CarNameProblem.None;

    /// <summary>Saves the name and closes the box (Enter).</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (_store.TrySet(CarOrdinal, Text) == CarNameProblem.None)
        {
            IsOpen = false;
        }
    }

    /// <summary>Closes the box without changing anything (Esc).</summary>
    [RelayCommand]
    private void Cancel() => IsOpen = false;

    /// <summary>Forgets the nickname, so the car shows its real name (or "Car #1234" when the list does not know it) again, and closes the box.</summary>
    [RelayCommand]
    private void UseDefault()
    {
        _store.Remove(CarOrdinal);
        IsOpen = false;
    }
}
