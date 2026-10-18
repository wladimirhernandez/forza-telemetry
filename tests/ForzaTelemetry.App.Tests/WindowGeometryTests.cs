using System.Windows;

namespace ForzaTelemetry.App.Tests;

public class WindowGeometryTests
{
    // A 1920x1040 work area (1080 minus a 40 px taskbar) with its origin at the screen corner.
    private static readonly Rect WorkArea = new(0, 0, 1920, 1040);

    [Fact]
    public void FitSize_caps_each_dimension_to_the_work_area()
    {
        var size = WindowGeometry.FitSize(new Size(1440, 900), new Rect(0, 0, 1366, 728));

        Assert.Equal(new Size(1366, 728), size);
    }

    [Fact]
    public void FitSize_keeps_a_size_that_already_fits()
    {
        Assert.Equal(new Size(1440, 900), WindowGeometry.FitSize(new Size(1440, 900), WorkArea));
    }

    [Fact]
    public void CenterIn_centers_on_a_work_area_that_is_not_at_the_origin()
    {
        // Secondary monitor to the right of the primary, taskbar at the top (work area starts at y=48).
        var point = WindowGeometry.CenterIn(new Rect(1920, 48, 1600, 852), new Size(1000, 600));

        Assert.Equal(new Point(2220, 174), point);
    }

    [Fact]
    public void RestoreUnderCursor_keeps_the_cursor_proportion_of_the_title_bar()
    {
        // Cursor at the exact middle of the maximized bar -> middle of the 1440 px restored window.
        var point = WindowGeometry.RestoreUnderCursor(new Point(960, 20), WorkArea, new Size(1440, 900), 12);

        Assert.Equal(240, point.X);
    }

    [Fact]
    public void RestoreUnderCursor_places_the_title_bar_at_the_top_of_the_work_area()
    {
        var point = WindowGeometry.RestoreUnderCursor(new Point(960, 20), WorkArea, new Size(1440, 900), 12);

        Assert.Equal(-12, point.Y); // the 12 px shadow margin sits above the work area, the card starts at its top
    }

    [Fact]
    public void RestoreUnderCursor_clamps_to_the_left_edge()
    {
        var point = WindowGeometry.RestoreUnderCursor(new Point(0, 20), WorkArea, new Size(1440, 900), 12);

        Assert.Equal(0, point.X);
    }

    [Fact]
    public void RestoreUnderCursor_clamps_to_the_right_edge()
    {
        var point = WindowGeometry.RestoreUnderCursor(new Point(1920, 20), WorkArea, new Size(1440, 900), 12);

        Assert.Equal(480, point.X); // 1920 - 1440: the whole window stays on this monitor
    }

    [Fact]
    public void RestoreUnderCursor_works_on_a_monitor_with_a_non_zero_origin()
    {
        var work = new Rect(1920, 0, 1600, 860);

        var point = WindowGeometry.RestoreUnderCursor(new Point(2720, 10), work, new Size(1000, 700), 12);

        Assert.Equal(2220, point.X); // cursor at 50% of the monitor -> 50% of the restored window
    }
}
