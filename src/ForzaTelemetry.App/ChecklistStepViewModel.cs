using CommunityToolkit.Mvvm.ComponentModel;
using ForzaTelemetry.App.Resources;

namespace ForzaTelemetry.App;

/// <summary>One line of the connection checklist: a title, the state icon, and a sentence that says what is going on.</summary>
public sealed partial class ChecklistStepViewModel(string title) : ObservableObject
{
    /// <summary>The name of the line ("Packets arriving"); it can include a value (the port).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    private string _title = title;

    /// <summary>Where the line stands; the view maps it to an icon and a color.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    private CheckState _state;

    /// <summary>What is happening, in a sentence (for example the sender's address, or why it is waiting).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    private string _detail = "";

    /// <summary>The state as a word, so the line does not rely on the icon's color alone.</summary>
    public string StateText => State switch
    {
        CheckState.Ok => SetupStrings.SetupStateOk,
        CheckState.Warning => SetupStrings.SetupStateWarning,
        _ => SetupStrings.SetupStatePending
    };

    /// <summary>What a screen reader announces for the whole line: "Packets arriving: done. Receiving data from 192.168.0.30."</summary>
    public string AutomationName => $"{Title}: {StateText}. {Detail}";
}
