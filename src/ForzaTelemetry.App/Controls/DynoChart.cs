using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.Core.Insights;

namespace ForzaTelemetry.App.Controls;

/// <summary>
/// Power and torque against engine RPM, like a dyno sheet: two curves on a shared grid, the left axis for power (kW),
/// the right axis for torque (N·m), and labeled markers at the two peaks.
/// </summary>
/// <remarks>
/// <para>
/// <b>Units.</b> There is no power-unit setting in the app, so the chart is honest about both: the axis is in kW (the
/// unit the telemetry reports, after watts / 1000) and every peak label also shows mechanical horsepower, the number
/// players know from the game's UI. Putting kW and hp ticks on one axis would need either two scales fighting for the
/// same grid or labels like "250 / 335" on every line; a single metric axis plus the peak label in both units stays
/// readable. Torque has its own axis on the right. Both axes start at zero and have the same number of divisions
/// (<see cref="ChartMath.FitSharedAxes"/>), so the horizontal gridlines serve both curves.
/// </para>
/// <para>
/// <b>Smoothing.</b> The captured curve keeps the best value seen per 100 rpm bin, which is slightly jagged. The line is
/// drawn through a 3-point moving average over adjacent bins (<see cref="DynoMath.Smooth"/>) and never across a gap in
/// the data, and the raw bins are drawn as small dots so the smoothing cannot hide what was really measured. Peak
/// markers sit on the drawn (smoothed) line.
/// </para>
/// </remarks>
public sealed class DynoChart : FrameworkElement
{
    private const double GutterLeft = 58;
    private const double GutterRight = 58;
    private const double PlotTop = 34;
    private const double PlotBottomMargin = 30;

    public static readonly DependencyProperty CurveProperty = DependencyProperty.Register(
        nameof(Curve), typeof(IReadOnlyList<DynoPoint>), typeof(DynoChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnCurveChanged));

    /// <summary>Bump when the curve content changed even if the list instance is the same.</summary>
    public static readonly DependencyProperty CurveVersionProperty = DependencyProperty.Register(
        nameof(CurveVersion), typeof(int), typeof(DynoChart),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, OnCurveChanged));

    private static readonly Pen GridPen = ChartPalette.Pen(ChartPalette.Solid(ChartPalette.Hairline, 0.6), 1);
    private static readonly Pen AxisPen = ChartPalette.Pen(ChartPalette.HairlineBrush, 1);
    private static readonly Pen PowerPen = ChartPalette.Pen(ChartPalette.WarmBrush, 2.2);
    private static readonly Pen TorquePen = ChartPalette.Pen(ChartPalette.ColdBrush, 2.2);
    private static readonly Pen PeakGuidePen = CreateGuidePen();
    private static readonly Brush PowerFill = FadeDown(ChartPalette.Warm, 0.22);
    private static readonly Brush PowerDot = ChartPalette.Solid(ChartPalette.Warm, 0.55);
    private static readonly Brush TorqueDot = ChartPalette.Solid(ChartPalette.Cold, 0.55);
    private static readonly Brush ChipBackground = ChartPalette.Solid(ChartPalette.CardRaised, 0.96);
    private static readonly Pen ChipPowerBorder = ChartPalette.Pen(ChartPalette.Solid(ChartPalette.Warm, 0.7), 1);
    private static readonly Pen ChipTorqueBorder = ChartPalette.Pen(ChartPalette.Solid(ChartPalette.Cold, 0.7), 1);
    private static readonly Pen MarkerRing = ChartPalette.Pen(ChartPalette.Solid(ChartPalette.Card), 2);

    private readonly ChartText _text = new();
    private bool _dirty = true;
    private Size _builtFor;

    // Rebuilt state.
    private DynoPoint[] _smooth = [];
    private IReadOnlyList<DynoPoint> _raw = [];
    private StreamGeometry? _powerLine, _powerArea, _torqueLine;
    private double _xMin, _xMax, _powerMax, _powerStep, _torqueMax, _torqueStep;
    private int _powerPeak = -1, _torquePeak = -1;
    private int _divisions = 5;
    private double _left, _right, _top, _bottom;

    public DynoChart() => ClipToBounds = true;

    public IReadOnlyList<DynoPoint>? Curve
    {
        get => (IReadOnlyList<DynoPoint>?)GetValue(CurveProperty);
        set => SetValue(CurveProperty, value);
    }

    public int CurveVersion
    {
        get => (int)GetValue(CurveVersionProperty);
        set => SetValue(CurveVersionProperty, value);
    }

    private static void OnCurveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((DynoChart)d)._dirty = true;

    private static Pen CreateGuidePen()
    {
        var pen = new Pen(ChartPalette.Solid(ChartPalette.Text, 0.35), 1) { DashStyle = new DashStyle([3, 3], 0) };
        pen.Freeze();
        return pen;
    }

    private static Brush FadeDown(Color color, double topAlpha)
    {
        var brush = new LinearGradientBrush(
            ChartPalette.WithAlpha(color, topAlpha), ChartPalette.WithAlpha(color, 0), new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 260 || height < 160 || Curve is not { Count: > 0 })
        {
            return;
        }

        if (_dirty || _builtFor != new Size(width, height))
        {
            Rebuild(Curve, width, height);
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        DrawGrid(dc, dpi);
        dc.DrawGeometry(PowerFill, null, _powerArea);
        dc.DrawGeometry(null, TorquePen, _torqueLine);
        dc.DrawGeometry(null, PowerPen, _powerLine);

        // Raw bins as dots: honest about coverage, and shows where smoothing was applied.
        for (var i = 0; i < _raw.Count; i++)
        {
            dc.DrawEllipse(PowerDot, null, new Point(X(_raw[i].Rpm), PowerY(_raw[i].PowerWatts / 1000.0)), 1.6, 1.6);
            dc.DrawEllipse(TorqueDot, null, new Point(X(_raw[i].Rpm), TorqueY(_raw[i].TorqueNewtonMeters)), 1.6, 1.6);
        }

        DrawPeaks(dc, dpi);
    }

    private double X(double rpm) => _left + (rpm - _xMin) / (_xMax - _xMin) * (_right - _left);

    private double PowerY(double kw) => _bottom - Math.Clamp(kw / _powerMax, 0, 1) * (_bottom - _top);

    private double TorqueY(double nm) => _bottom - Math.Clamp(nm / _torqueMax, 0, 1) * (_bottom - _top);

    private void Rebuild(IReadOnlyList<DynoPoint> curve, double width, double height)
    {
        _dirty = false;
        _builtFor = new Size(width, height);
        _left = GutterLeft;
        _right = width - GutterRight;
        _top = PlotTop;
        _bottom = height - PlotBottomMargin;

        _raw = curve;
        _smooth = DynoMath.Smooth(curve);
        (_powerPeak, _torquePeak) = DynoMath.FindPeaks(_smooth);

        var peakKw = _smooth[_powerPeak].PowerWatts / 1000.0;
        var peakNm = _smooth[_torquePeak].TorqueNewtonMeters;
        (_divisions, _powerStep, _torqueStep) = ChartMath.FitSharedAxes(peakKw * 1.06, peakNm * 1.06);
        _powerMax = _powerStep * _divisions;
        _torqueMax = _torqueStep * _divisions;

        // RPM axis in whole thousands around the captured range, at least two thousand wide.
        _xMin = Math.Floor(Math.Max(0, curve[0].Rpm - 100) / 1000) * 1000;
        _xMax = Math.Max(Math.Ceiling((curve[^1].Rpm + 100) / 1000) * 1000, _xMin + 2000);

        var power = new StreamGeometry();
        var area = new StreamGeometry();
        var torque = new StreamGeometry();
        using (var pc = power.Open())
        using (var ac = area.Open())
        using (var tc = torque.Open())
        {
            var runStart = 0;
            for (var i = 1; i <= _smooth.Length; i++)
            {
                if (i < _smooth.Length && _smooth[i].Rpm - _smooth[i - 1].Rpm <= DynoMath.MaxLinkedGapRpm)
                {
                    continue;
                }

                EmitRun(pc, ac, tc, runStart, i);
                runStart = i;
            }
        }

        power.Freeze();
        area.Freeze();
        torque.Freeze();
        _powerLine = power;
        _powerArea = area;
        _torqueLine = torque;
    }

    private void EmitRun(StreamGeometryContext power, StreamGeometryContext area, StreamGeometryContext torque, int from, int to)
    {
        if (to - from < 2)
        {
            return; // a single isolated bin is shown by its dot only
        }

        var powerPoints = new List<Point>(to - from);
        var torquePoints = new List<Point>(to - from);
        for (var i = from; i < to; i++)
        {
            powerPoints.Add(new Point(X(_smooth[i].Rpm), PowerY(_smooth[i].PowerWatts / 1000.0)));
            torquePoints.Add(new Point(X(_smooth[i].Rpm), TorqueY(_smooth[i].TorqueNewtonMeters)));
        }

        power.BeginFigure(powerPoints[0], false, false);
        power.PolyLineTo(powerPoints.Skip(1).ToList(), true, true);
        torque.BeginFigure(torquePoints[0], false, false);
        torque.PolyLineTo(torquePoints.Skip(1).ToList(), true, true);

        area.BeginFigure(new Point(powerPoints[0].X, _bottom), true, true);
        area.PolyLineTo(powerPoints, false, false);
        area.LineTo(new Point(powerPoints[^1].X, _bottom), false, false);
    }

    private void DrawGrid(DrawingContext dc, double dpi)
    {
        var plotHeight = _bottom - _top;
        for (var i = 0; i <= _divisions; i++)
        {
            var y = Math.Floor(_bottom - plotHeight * i / _divisions) + 0.5;
            dc.DrawLine(i == 0 ? AxisPen : GridPen, new Point(_left, y), new Point(_right, y));
            _text.DrawRight(dc, (i * _powerStep).ToString("0.##", CultureInfo.CurrentCulture), _left - 10, y - 8, 11, ChartPalette.WarmBrush, dpi, numeral: true);
            _text.Draw(dc, (i * _torqueStep).ToString("0.##", CultureInfo.CurrentCulture), _right + 10, y - 8, 11, ChartPalette.ColdBrush, dpi, numeral: true);
        }

        var xStep = ChartMath.NiceStep(_xMax - _xMin, 7);
        for (var rpm = _xMin; rpm <= _xMax + 1e-6; rpm += xStep)
        {
            var x = Math.Floor(X(rpm)) + 0.5;
            dc.DrawLine(GridPen, new Point(x, _top), new Point(x, _bottom));
            _text.DrawCentered(dc, rpm.ToString("#,0", CultureInfo.CurrentCulture), x, _bottom + 8, 11, ChartPalette.MutedBrush, dpi, numeral: true);
        }

        _text.DrawRight(dc, InsightsStrings.DynoPowerAxis, _left - 10, _top - 26, 12, ChartPalette.WarmBrush, dpi, bold: true);
        _text.Draw(dc, InsightsStrings.DynoTorqueAxis, _right + 10, _top - 26, 12, ChartPalette.ColdBrush, dpi, bold: true);
        _text.DrawRight(dc, InsightsStrings.DynoRpmAxis, _right + 34, _bottom + 8, 11, ChartPalette.MutedBrush, dpi);
    }

    private void DrawPeaks(DrawingContext dc, double dpi)
    {
        var power = _smooth[_powerPeak];
        var torque = _smooth[_torquePeak];
        var powerPoint = new Point(X(power.Rpm), PowerY(power.PowerWatts / 1000.0));
        var torquePoint = new Point(X(torque.Rpm), TorqueY(torque.TorqueNewtonMeters));

        // A guide from each peak down to the rpm axis shows WHERE in the rev range it happens.
        dc.DrawLine(PeakGuidePen, powerPoint, new Point(powerPoint.X, _bottom));
        dc.DrawLine(PeakGuidePen, torquePoint, new Point(torquePoint.X, _bottom));
        dc.DrawEllipse(ChartPalette.WarmBrush, MarkerRing, powerPoint, 5, 5);
        dc.DrawEllipse(ChartPalette.ColdBrush, MarkerRing, torquePoint, 5, 5);

        var powerText = string.Format(
            CultureInfo.CurrentCulture, InsightsStrings.DynoPeakPowerFormat,
            (power.PowerWatts / 1000.0).ToString("0", CultureInfo.CurrentCulture),
            DynoMath.ToHorsepower(power.PowerWatts).ToString("0", CultureInfo.CurrentCulture),
            power.Rpm.ToString("#,0", CultureInfo.CurrentCulture));
        var torqueText = string.Format(
            CultureInfo.CurrentCulture, InsightsStrings.DynoPeakTorqueFormat,
            torque.TorqueNewtonMeters.ToString("0", CultureInfo.CurrentCulture),
            torque.Rpm.ToString("#,0", CultureInfo.CurrentCulture));

        var powerChip = Chip(dc, powerText, InsightsStrings.DynoPeakPower, powerPoint, above: true, dpi, ChipPowerBorder, ChartPalette.WarmBrush, null);
        Chip(dc, torqueText, InsightsStrings.DynoPeakTorque, torquePoint, above: true, dpi, ChipTorqueBorder, ChartPalette.ColdBrush, powerChip);
    }

    /// <summary>Draws a label chip near a marker, clamped to the plot, nudged away from <paramref name="avoid"/>.</summary>
    private Rect Chip(DrawingContext dc, string value, string title, Point anchor, bool above, double dpi, Pen border, Brush accent, Rect? avoid)
    {
        var titleText = _text.Get(title, 11, ChartPalette.MutedBrush, dpi);
        var valueText = _text.Get(value, 13, ChartPalette.TextBrush, dpi, numeral: true);
        var width = Math.Max(titleText.Width, valueText.Width) + 28;
        const double height = 46;

        var x = Math.Clamp(anchor.X - width / 2, _left + 4, _right - width - 4);
        var y = above ? anchor.Y - height - 14 : anchor.Y + 14;
        y = Math.Clamp(y, _top + 2, _bottom - height - 2);
        var rect = new Rect(x, y, width, height);

        // If it lands on the other chip, drop below the marker instead (or shift down as a last resort).
        if (avoid is { } other && rect.IntersectsWith(other))
        {
            rect.Y = Math.Min(anchor.Y + 14, _bottom - height - 2);
            if (rect.IntersectsWith(other))
            {
                rect.Y = Math.Min(other.Bottom + 6, _bottom - height - 2);
            }
        }

        dc.DrawRoundedRectangle(ChipBackground, border, rect, 9, 9);
        _text.Draw(dc, title, rect.X + 14, rect.Y + 6, 11, ChartPalette.MutedBrush, dpi);
        dc.DrawText(valueText, new Point(rect.X + 14, rect.Y + 21));
        dc.DrawRoundedRectangle(accent, null, new Rect(rect.X + 5, rect.Y + 10, 3, rect.Height - 20), 1.5, 1.5);
        return rect;
    }
}
