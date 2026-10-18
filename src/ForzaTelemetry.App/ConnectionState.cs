namespace ForzaTelemetry.App;

/// <summary>
/// Coarse connection state the view maps to a colour (status pill dot). Kept separate from
/// <see cref="ShellViewModel.Status"/>, which is the human-readable sentence: the view needs a closed set to
/// switch on, not text to parse.
/// </summary>
public enum ConnectionState
{
    /// <summary>Not listening.</summary>
    Stopped,

    /// <summary>Listening, but no packet has arrived yet.</summary>
    Listening,

    /// <summary>Packets are arriving.</summary>
    Receiving,

    /// <summary>Was receiving, but the stream went silent past the threshold.</summary>
    NoData,

    /// <summary>A recording is being played back instead of the game (the replay colour in the top bar).</summary>
    Replaying,

    /// <summary>The service faulted (for example the port is already in use).</summary>
    Error
}
