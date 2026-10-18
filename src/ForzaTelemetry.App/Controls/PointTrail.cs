namespace ForzaTelemetry.App.Controls;

/// <summary>
/// Fixed-size ring buffer of the most recent 2D samples (newest overwrites oldest). The G-force meter draws one dot
/// per sample with a fading alpha. Allocating the arrays once and reusing the slots means pushing a sample is free of
/// garbage, which matters because it happens on every UI refresh (~20 times a second) for as long as the app runs.
/// </summary>
public sealed class PointTrail
{
    private readonly double[] _x;
    private readonly double[] _y;
    private int _next; // slot the NEXT push writes to

    public PointTrail(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _x = new double[capacity];
        _y = new double[capacity];
    }

    public int Capacity => _x.Length;

    /// <summary>How many samples are stored (grows to <see cref="Capacity"/>, then stays there).</summary>
    public int Count { get; private set; }

    public void Push(double x, double y)
    {
        _x[_next] = x;
        _y[_next] = y;
        _next = (_next + 1) % _x.Length;
        if (Count < _x.Length)
        {
            Count++;
        }
    }

    /// <summary>
    /// Pushes only when the point differs from the newest stored one, so a re-render that carries no new data
    /// (resize, DPI change) cannot fill the trail with duplicates. Returns whether a sample was stored.
    /// </summary>
    public bool PushIfChanged(double x, double y)
    {
        if (Count > 0 && FromNewest(0) == (x, y))
        {
            return false;
        }

        Push(x, y);
        return true;
    }

    /// <summary>Reads a sample by age: 0 = newest, <c>Count - 1</c> = oldest.</summary>
    public (double X, double Y) FromNewest(int age)
    {
        if ((uint)age >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(age));
        }

        // _next points one past the newest sample, so the newest is at _next - 1, the one before at _next - 2, ...
        var slot = ((_next - 1 - age) % _x.Length + _x.Length) % _x.Length;
        return (_x[slot], _y[slot]);
    }

    public void Clear()
    {
        Count = 0;
        _next = 0;
    }
}
