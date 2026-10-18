namespace ForzaTelemetry.App;

/// <summary>
/// Identity of each top-level screen. A closed enum (not a string) so the Ctrl+1..8 key bindings, the tab strip and the
/// tests all refer to a screen the compiler can check, and so the tab order is declared in exactly one place
/// (<see cref="ShellViewModel"/>'s tab list).
/// </summary>
public enum PageId
{
    Dashboard,
    Performance,
    Laps,
    Sessions,
    Analysis,
    Map,
    Tuning,
    Settings
}
