using ForzaTelemetry.App.Controls;

namespace ForzaTelemetry.App.Tests;

public class PointTrailTests
{
    [Fact]
    public void A_new_trail_is_empty()
    {
        var trail = new PointTrail(3);

        Assert.Equal(0, trail.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => trail.FromNewest(0));
    }

    [Fact]
    public void Samples_are_read_newest_first()
    {
        var trail = new PointTrail(3);
        trail.Push(1, 10);
        trail.Push(2, 20);

        Assert.Equal(2, trail.Count);
        Assert.Equal((2.0, 20.0), trail.FromNewest(0));
        Assert.Equal((1.0, 10.0), trail.FromNewest(1));
    }

    [Fact]
    public void When_full_the_oldest_sample_is_overwritten()
    {
        var trail = new PointTrail(3);
        for (var i = 1; i <= 5; i++)
        {
            trail.Push(i, -i);
        }

        Assert.Equal(3, trail.Count);
        Assert.Equal((5.0, -5.0), trail.FromNewest(0));
        Assert.Equal((4.0, -4.0), trail.FromNewest(1));
        Assert.Equal((3.0, -3.0), trail.FromNewest(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => trail.FromNewest(3));
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        var trail = new PointTrail(2);
        trail.Push(1, 1);

        trail.Clear();

        Assert.Equal(0, trail.Count);
    }

    [Fact]
    public void Capacity_must_be_positive() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PointTrail(0));
}
