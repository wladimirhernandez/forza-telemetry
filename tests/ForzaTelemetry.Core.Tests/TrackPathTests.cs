using ForzaTelemetry.Core.Insights;

namespace ForzaTelemetry.Core.Tests;

public class TrackPathTests
{
    private static TelemetryPacket At(float x, float z, byte brake = 0, float speed = 30f) =>
        TestPackets.Drive(0, speed, 0f) with { PositionX = x, PositionZ = z, Brake = brake };

    [Fact]
    public void Adds_a_point_only_after_the_minimum_spacing()
    {
        var path = new TrackPath(minSpacingMeters: 2f);

        path.OnPacket(At(0, 0));
        path.OnPacket(At(0.5f, 0));
        path.OnPacket(At(1.9f, 0));
        path.OnPacket(At(2.0f, 0));
        path.OnPacket(At(3.0f, 0));
        path.OnPacket(At(4.0f, 0));

        Assert.Equal([0f, 2f, 4f], path.ToArray().Select(p => p.X));
    }

    [Fact]
    public void Spacing_uses_euclidean_distance_in_the_ground_plane()
    {
        var path = new TrackPath(minSpacingMeters: 2f);

        path.OnPacket(At(0, 0));
        path.OnPacket(At(1.5f, 1.5f)); // 2.12 m away diagonally

        Assert.Equal(2, path.Count);
    }

    [Fact]
    public void Flags_braking_points_above_ten_percent_brake()
    {
        var path = new TrackPath();

        path.OnPacket(At(0, 0, brake: 0));
        path.OnPacket(At(5, 0, brake: 26)); // 10.2%
        path.OnPacket(At(10, 0, brake: 25)); // 9.8%

        Assert.Equal([false, true, false], path.ToArray().Select(p => p.Braking));
    }

    [Fact]
    public void Braking_between_stored_points_is_not_lost()
    {
        var path = new TrackPath(minSpacingMeters: 2f);

        path.OnPacket(At(0, 0));
        path.OnPacket(At(0.5f, 0, brake: 200)); // too close to store, but a braking mark

        Assert.True(Assert.Single(path.ToArray()).Braking);
    }

    [Fact]
    public void Stores_speed_with_each_point()
    {
        var path = new TrackPath();

        path.OnPacket(At(0, 0, speed: 12f));

        Assert.Equal(12f, Assert.Single(path.ToArray()).SpeedMs);
    }

    [Fact]
    public void A_teleport_starts_a_new_segment()
    {
        var path = new TrackPath();

        path.OnPacket(At(0, 0));
        path.OnPacket(At(10, 0));
        path.OnPacket(At(5000, 0));

        Assert.Equal([true, false, true], path.ToArray().Select(p => p.StartsSegment));
    }

    [Fact]
    public void Pausing_starts_a_new_segment_and_stores_nothing_while_paused()
    {
        var path = new TrackPath();
        path.OnPacket(At(0, 0));

        path.OnPacket(At(3, 0) with { IsRaceOn = false });
        path.OnPacket(At(10, 0));

        var points = path.ToArray();
        Assert.Equal(2, points.Length);
        Assert.True(points[1].StartsSegment);
    }

    [Fact]
    public void Thins_itself_when_the_cap_is_reached_but_keeps_covering_the_whole_session()
    {
        var path = new TrackPath(minSpacingMeters: 1f, maxPoints: 100);

        for (var i = 0; i < 1000; i++)
        {
            path.OnPacket(At(i, 0)); // a straight 1 km, one packet per metre
        }

        var points = path.ToArray();
        Assert.True(points.Length <= 100);
        Assert.True(points.Length > 25); // thinned, not wiped
        Assert.Equal(0f, points[0].X);
        Assert.True(points[^1].X > 900f); // still reaches the end
        Assert.Equal(points.OrderBy(p => p.X).Select(p => p.X), points.Select(p => p.X)); // still in order
    }

    [Fact]
    public void Thinning_keeps_braking_marks()
    {
        var path = new TrackPath(minSpacingMeters: 1f, maxPoints: 8);
        for (var i = 0; i < 7; i++)
        {
            path.OnPacket(At(i, 0, brake: i == 3 ? (byte)255 : (byte)0));
        }

        path.OnPacket(At(7, 0)); // reaches the cap: thin

        Assert.Contains(path.ToArray(), p => p.Braking);
    }

    [Fact]
    public void Version_changes_only_when_the_points_change()
    {
        var path = new TrackPath(minSpacingMeters: 2f);
        path.OnPacket(At(0, 0));
        var v1 = path.Version;

        path.OnPacket(At(0.1f, 0)); // dropped, no braking
        Assert.Equal(v1, path.Version);

        path.OnPacket(At(5, 0));
        Assert.NotEqual(v1, path.Version);
    }

    [Fact]
    public void CopyTo_fills_a_caller_buffer_without_exceeding_it()
    {
        var path = new TrackPath();
        for (var i = 0; i < 5; i++)
        {
            path.OnPacket(At(i * 3, 0));
        }

        var buffer = new TrackPoint[3];

        Assert.Equal(3, path.CopyTo(buffer));
        Assert.Equal(3f, buffer[1].X);
    }

    [Fact]
    public void Clear_forgets_the_path()
    {
        var path = new TrackPath();
        path.OnPacket(At(0, 0));

        path.Clear();

        Assert.Equal(0, path.Count);
    }
}
