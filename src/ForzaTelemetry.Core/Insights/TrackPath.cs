namespace ForzaTelemetry.Core.Insights;

/// <summary>One vertex of the driven path.</summary>
/// <param name="X">World X in meters.</param>
/// <param name="Z">World Z in meters (the ground plane is X/Z).</param>
/// <param name="SpeedMs">Speed at this point, m/s.</param>
/// <param name="Braking">True when the brake was pressed (above 10%).</param>
/// <param name="StartsSegment">True when the path jumped here (teleport/restart), so the line must not be drawn from the previous point.</param>
public readonly record struct TrackPoint(float X, float Z, float SpeedMs, bool Braking, bool StartsSegment);

/// <summary>
/// The driven path for the whole session, thinned by distance so it stays small.
/// </summary>
/// <remarks>
/// <para><b>Distance-based decimation.</b> At 60 Hz a fast car adds a point every ~1 m while a slow one adds
/// several per metre, so keeping every sample wastes memory and draws identical pixels. A point is stored only
/// when the car moved at least <see cref="MinSpacingMeters"/> from the last stored one; the path density is then
/// uniform regardless of speed.</para>
///
/// <para><b>Cap.</b> To hold a whole session in bounded memory, when <see cref="MaxPoints"/> is reached every
/// second point is dropped and the spacing doubles. The shape is preserved (just coarser) and the cost is
/// amortized O(1) per point. Braking flags survive when either of the merged points had one.</para>
///
/// <para><b>Thread-safety.</b> Single writer, UI readers: an internal lock guards the list. Readers use
/// <see cref="CopyTo"/> or <see cref="ToArray"/>, and <see cref="Version"/> to skip rebuilding geometry when
/// nothing changed.</para>
/// </remarks>
public sealed class TrackPath : IPacketObserver
{
    /// <summary>Brake level (0..1) above which a point is flagged as braking.</summary>
    public const float BrakingThreshold = 0.1f;

    /// <summary>A gap larger than this between consecutive packets is a teleport, not driving.</summary>
    private const float JumpMeters = 100f;

    private readonly object _gate = new();
    private readonly List<TrackPoint> _points = [];
    private readonly int _maxPoints;
    private float _spacing;
    private int _version;
    private float _rawX;
    private float _rawZ;
    private bool _hasRaw;

    /// <summary>Creates a path with the given minimum spacing and cap.</summary>
    public TrackPath(float minSpacingMeters = 2f, int maxPoints = 20000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minSpacingMeters, 0f);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPoints, 4);
        _spacing = minSpacingMeters;
        _maxPoints = maxPoints;
        MinSpacingMeters = minSpacingMeters;
    }

    /// <summary>Initial minimum spacing between stored points, meters.</summary>
    public float MinSpacingMeters { get; }

    /// <summary>Maximum number of points before the path is thinned.</summary>
    public int MaxPoints => _maxPoints;

    /// <summary>Increments whenever the stored points change.</summary>
    public int Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    /// <summary>Number of stored points.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _points.Count;
            }
        }
    }

    /// <inheritdoc />
    public void OnPacket(TelemetryPacket packet)
    {
        if (!packet.IsRaceOn || !float.IsFinite(packet.PositionX) || !float.IsFinite(packet.PositionZ))
        {
            // Paused/menu packets carry no usable position. We deliberately do NOT mark a new segment here:
            // pausing in place and resuming must continue the same line. If the car really was moved while
            // paused, the raw gap check below detects it on the first packet after the pause.
            return;
        }

        lock (_gate)
        {
            var braking = packet.Brake / 255f > BrakingThreshold;
            var x = packet.PositionX;
            var z = packet.PositionZ;

            // A jump is judged on the gap between consecutive PACKETS (raw positions), never on the gap to the
            // last stored point: stored points get sparser as the path is thinned, so that gap would grow
            // until every point looked like a teleport.
            var jumped = false;
            if (_hasRaw)
            {
                var rx = x - _rawX;
                var rz = z - _rawZ;
                jumped = (rx * rx) + (rz * rz) > JumpMeters * JumpMeters;
            }

            _rawX = x;
            _rawZ = z;
            _hasRaw = true;

            if (_points.Count == 0 || jumped)
            {
                // First point ever (or after Clear) and every post-jump point always starts a segment.
                _points.Add(new TrackPoint(x, z, packet.Speed, braking, StartsSegment: true));
                _version++;
            }
            else
            {
                var last = _points[^1];
                var dx = x - last.X;
                var dz = z - last.Z;
                if (MathF.Sqrt((dx * dx) + (dz * dz)) < _spacing)
                {
                    // Too close to add, but never lose a braking mark: fold it into the last point.
                    if (braking && !last.Braking)
                    {
                        _points[^1] = last with { Braking = true };
                        _version++;
                    }

                    return;
                }

                _points.Add(new TrackPoint(x, z, packet.Speed, braking, StartsSegment: false));
                _version++;
            }

            if (_points.Count >= _maxPoints)
            {
                Thin();
            }
        }
    }

    /// <summary>Copies the stored points into <paramref name="destination"/> (as many as fit); returns the count copied.</summary>
    public int CopyTo(Span<TrackPoint> destination)
    {
        lock (_gate)
        {
            var n = Math.Min(_points.Count, destination.Length);
            for (var i = 0; i < n; i++)
            {
                destination[i] = _points[i];
            }

            return n;
        }
    }

    /// <summary>Allocating snapshot of every stored point.</summary>
    public TrackPoint[] ToArray()
    {
        lock (_gate)
        {
            return [.. _points];
        }
    }

    /// <summary>Forgets the path (for the "clear" button or a new session).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _points.Clear();
            _spacing = MinSpacingMeters;
            _hasRaw = false;
            _version++;
        }
    }

    /// <summary>
    /// Drops every second point and doubles the spacing. A pair whose second point starts a segment keeps BOTH
    /// points: dropping the first would shorten the previous segment's end, dropping the second would lose the
    /// segment start and draw a line across the jump.
    /// </summary>
    private void Thin()
    {
        var kept = new List<TrackPoint>((_points.Count / 2) + 2);
        var i = 0;
        while (i < _points.Count)
        {
            var point = _points[i];
            if (i + 1 >= _points.Count)
            {
                kept.Add(point);
                break;
            }

            var next = _points[i + 1];
            if (next.StartsSegment)
            {
                kept.Add(point);
                kept.Add(next);
            }
            else
            {
                kept.Add(point with { Braking = point.Braking || next.Braking });
            }

            i += 2;
        }

        // Pathological input (a jump every few points) could stop the list from shrinking; never exceed the cap.
        if (kept.Count >= _maxPoints)
        {
            kept.RemoveRange(0, kept.Count - (_maxPoints / 2));
            kept[0] = kept[0] with { StartsSegment = true };
        }

        _points.Clear();
        _points.AddRange(kept);
        _spacing *= 2f;
    }
}
