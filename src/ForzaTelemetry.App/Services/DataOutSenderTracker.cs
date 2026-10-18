using System.Net;

namespace ForzaTelemetry.App.Services;

/// <summary>Who is sending Data Out datagrams to this PC right now. An interface so the connection checklist can be tested with a fake.</summary>
public interface IDataOutSenderInfo
{
    /// <summary>The address of the last sender of the current listening session, or null when nothing has arrived yet.</summary>
    string? LastSenderAddress { get; }
}

/// <summary>
/// Remembers the sender of the most recent datagram. The UDP listener calls <see cref="Record"/> from its receive thread; the UI
/// reads <see cref="LastSenderAddress"/> from the dispatcher thread.
/// </summary>
/// <remarks>
/// <b>Why it keeps the endpoint and not a string.</b> <see cref="Record"/> runs for every datagram (up to ~60 per second): storing the
/// reference the socket already allocated costs nothing, and the text is only built when the UI asks (a few times a second, and only
/// while the guide is open). A single reference write is atomic, so <see cref="Volatile"/> is all the synchronization it needs.
/// </remarks>
public sealed class DataOutSenderTracker : IDataOutSenderInfo
{
    private IPEndPoint? _last;

    /// <inheritdoc />
    public string? LastSenderAddress => Volatile.Read(ref _last)?.Address.ToString();

    /// <summary>Notes the sender of a datagram. Cheap, non-blocking, never throws (it runs inside the receive loop).</summary>
    public void Record(IPEndPoint sender) => Volatile.Write(ref _last, sender);

    /// <summary>Forgets the sender: a new listening session starts with nothing received.</summary>
    public void Reset() => Volatile.Write(ref _last, null);
}
