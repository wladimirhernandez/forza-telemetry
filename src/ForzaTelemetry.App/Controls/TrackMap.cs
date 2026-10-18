using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.Core.Insights;

namespace ForzaTelemetry.App.Controls;

/// <summary>
/// The driven path seen from above, colored by speed, with braking markers, a glowing dot for the car, pan and zoom.
/// </summary>
/// <remarks>
/// <para>
/// <b>Coloring a line by a value.</b> WPF strokes a geometry with ONE brush, so a path that changes color along its
/// length would need one pen change per segment: thousands of draw calls. Instead the speed range is quantized into
/// <see cref="Buckets"/> colors and one <see cref="StreamGeometry"/> is built per color; each holds every segment of
/// that color as connected polylines. A frame is then about 32 <c>DrawGeometry</c> calls however long the path is.
/// Underneath, one dark "casing" geometry of the whole path is drawn slightly wider, which separates the colored line
/// from the card and from crossing parts of the track.
/// </para>
/// <para>
/// <b>View.</b> The mapping from meters to pixels is a <see cref="MapViewport"/>. By default the map auto-fits the path
/// (and the car) with padding, preserving the aspect ratio, and re-fits as the path grows. The first wheel/drag gesture
/// switches to a manual view that stays put while data keeps arriving; double-click goes back to auto-fit. Geometry is
/// built in screen space (so line widths stay constant while zooming) and rebuilt when the data, the size or the view
/// changes, never on a plain re-render.
/// </para>
/// <para>
/// <b>Breaks.</b> A point flagged <see cref="TrackPoint.StartsSegment"/> begins a new polyline: after a teleport or a
/// restart no line is drawn across the jump.
/// </para>
/// </remarks>
public sealed class TrackMap : FrameworkElement
{
    private const int Buckets = 32;
    private const double Padding = 32;
    private const double Cull = 60;

    public static readonly DependencyProperty PointsProperty = Register<TrackPoint[]?>(nameof(Points), null, rebuild: true);
    public static readonly DependencyProperty PointCountProperty = Register(nameof(PointCount), 0, rebuild: true);
    public static readonly DependencyProperty DataVersionProperty = Register(nameof(DataVersion), 0, rebuild: true);
    public static readonly DependencyProperty MinSpeedMsProperty = Register(nameof(MinSpeedMs), 0.0, rebuild: true);
    public static readonly DependencyProperty MaxSpeedMsProperty = Register(nameof(MaxSpeedMs), 0.0, rebuild: true);

    /// <summary>Car position in world meters; moving it only re-renders (the dot), it does not rebuild the path.</summary>
    public static readonly DependencyProperty CurrentXProperty = Register(nameof(CurrentX), 0.0, rebuild: false);
    public static readonly DependencyProperty CurrentZProperty = Register(nameof(CurrentZ), 0.0, rebuild: false);
    public static readonly DependencyProperty HasCurrentProperty = Register(nameof(HasCurrent), false, rebuild: false);

    /// <summary>
    /// Pan/zoom state owned by the view model so it survives the view being rebuilt on tab switches. When null the
    /// control keeps a private one (fine for a standalone use).
    /// </summary>
    public static readonly DependencyProperty ViewStateProperty = DependencyProperty.Register(
        nameof(ViewState), typeof(MapViewState), typeof(TrackMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((TrackMap)d)._dirty = true));

    public static readonly DependencyProperty SpeedScaleProperty = Register(nameof(SpeedScale), 3.6, rebuild: false);
    public static readonly DependencyProperty SpeedUnitProperty = Register(nameof(SpeedUnit), "km/h", rebuild: false);

    private static readonly Pen[] BucketPens = CreateBucketPens();
    private static readonly Pen CasingPen = ChartPalette.Pen(ChartPalette.Solid(ChartPalette.Canvas, 0.9), 6);
    private static readonly Brush LegendRamp = CreateLegendRamp();
    private static readonly Pen MarkerRing = ChartPalette.Pen(ChartPalette.Solid(ChartPalette.Canvas), 1.5);
    private static readonly Pen DotRing = ChartPalette.Pen(ChartPalette.Solid(ChartPalette.Canvas), 2);
    private static readonly Brush GlowBrush = CreateGlow();
    private static readonly Brush LegendBackground = ChartPalette.Solid(ChartPalette.CardRaised, 0.92);
    private static readonly Pen LegendBorder = ChartPalette.Pen(ChartPalette.HairlineBrush, 1);
    private static readonly Brush HintBrush = ChartPalette.Solid(ChartPalette.Muted, 0.75);

    private readonly ChartText _text = new();
    private readonly StreamGeometry?[] _bucketGeometry = new StreamGeometry?[Buckets];
    private readonly List<Point> _brakeMarkers = [];
    private StreamGeometry? _casing;

    private bool _dirty = true;
    private Size _builtFor;
    private readonly MapViewState _ownState = new();
    private bool _dragging;
    private Point _dragLast;

    public MapViewState? ViewState { get => (MapViewState?)GetValue(ViewStateProperty); set => SetValue(ViewStateProperty, value); }

    private MapViewState State => ViewState ?? _ownState;

    public TrackMap()
    {
        ClipToBounds = true;
        Focusable = false;
    }

    public TrackPoint[]? Points { get => (TrackPoint[]?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }

    public int PointCount { get => (int)GetValue(PointCountProperty); set => SetValue(PointCountProperty, value); }

    public int DataVersion { get => (int)GetValue(DataVersionProperty); set => SetValue(DataVersionProperty, value); }

    /// <summary>Slowest speed of the session, m/s: the cold end of the ramp.</summary>
    public double MinSpeedMs { get => (double)GetValue(MinSpeedMsProperty); set => SetValue(MinSpeedMsProperty, value); }

    /// <summary>Fastest speed of the session, m/s: the red end of the ramp.</summary>
    public double MaxSpeedMs { get => (double)GetValue(MaxSpeedMsProperty); set => SetValue(MaxSpeedMsProperty, value); }

    public double CurrentX { get => (double)GetValue(CurrentXProperty); set => SetValue(CurrentXProperty, value); }

    public double CurrentZ { get => (double)GetValue(CurrentZProperty); set => SetValue(CurrentZProperty, value); }

    public bool HasCurrent { get => (bool)GetValue(HasCurrentProperty); set => SetValue(HasCurrentProperty, value); }

    /// <summary>Multiplier from m/s to the displayed speed unit, used only for the legend labels.</summary>
    public double SpeedScale { get => (double)GetValue(SpeedScaleProperty); set => SetValue(SpeedScaleProperty, value); }

    public string SpeedUnit { get => (string)GetValue(SpeedUnitProperty); set => SetValue(SpeedUnitProperty, value); }

    private static DependencyProperty Register<T>(string name, T defaultValue, bool rebuild) =>
        DependencyProperty.Register(
            name, typeof(T), typeof(TrackMap),
            new FrameworkPropertyMetadata(
                defaultValue,
                FrameworkPropertyMetadataOptions.AffectsRender,
                rebuild ? (d, _) => ((TrackMap)d)._dirty = true : null));

    private static Pen[] CreateBucketPens()
    {
        var pens = new Pen[Buckets];
        for (var i = 0; i < Buckets; i++)
        {
            pens[i] = ChartPalette.Pen(ChartPalette.Solid(SpeedRamp.Evaluate((i + 0.5) / Buckets)), 3);
        }

        return pens;
    }

    private static Brush CreateLegendRamp()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (var i = 0; i <= 8; i++)
        {
            brush.GradientStops.Add(new GradientStop(SpeedRamp.Evaluate(i / 8.0), i / 8.0));
        }

        brush.Freeze();
        return brush;
    }

    private static Brush CreateGlow()
    {
        // A soft halo: bright in the middle, fading to nothing, so the dot reads as light rather than as a sticker.
        var brush = new RadialGradientBrush();
        brush.GradientStops.Add(new GradientStop(ChartPalette.WithAlpha(ChartPalette.Text, 0.55), 0));
        brush.GradientStops.Add(new GradientStop(ChartPalette.WithAlpha(ChartPalette.Text, 0.18), 0.45));
        brush.GradientStops.Add(new GradientStop(ChartPalette.WithAlpha(ChartPalette.Text, 0), 1));
        brush.Freeze();
        return brush;
    }

    // ---- Input ------------------------------------------------------------------------------------

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var state = State;
        if (!state.HasUsableView)
        {
            return;
        }

        // Exponential: every wheel notch (120) zooms by the same RATIO, so zooming feels uniform at any scale.
        var factor = Math.Pow(1.0015, e.Delta);
        state.Viewport = state.Viewport.ZoomAt(e.GetPosition(this), factor, state.FitScale * 0.4, state.FitScale * MapViewport.MaxZoomFactor);
        state.Manual = true;
        _dirty = true;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            State.Manual = false;
            _dirty = true;
            InvalidateVisual();
            return;
        }

        _dragging = true;
        _dragLast = e.GetPosition(this);
        CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
        {
            return;
        }

        var position = e.GetPosition(this);
        var state = State;
        if (state.HasUsableView)
        {
            state.Viewport = state.Viewport.Pan(position.X - _dragLast.X, position.Y - _dragLast.Y);
            state.Manual = true;
        }

        _dragLast = position;
        _dirty = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        EndDrag();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndDrag();
    }

    private void EndDrag()
    {
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
            ClearValue(CursorProperty);
        }
    }

    // ---- Rendering --------------------------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 120 || height < 100)
        {
            return;
        }

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var points = Points is { } array ? array.AsSpan(0, Math.Clamp(PointCount, 0, array.Length)) : default;
        if (_dirty || _builtFor != new Size(width, height))
        {
            Rebuild(points, width, height);
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (!points.IsEmpty)
        {
            dc.DrawGeometry(null, CasingPen, _casing);
            for (var i = 0; i < Buckets; i++)
            {
                if (_bucketGeometry[i] is { } geometry)
                {
                    dc.DrawGeometry(null, BucketPens[i], geometry);
                }
            }

            foreach (var marker in _brakeMarkers)
            {
                dc.DrawEllipse(ChartPalette.BrakeBrush, MarkerRing, marker, 4, 4);
            }

            DrawCar(dc);
            DrawLegend(dc, dpi, height);
            _text.DrawRight(dc, InsightsStrings.MapNavigationHint, width - 14, height - 22, 11, HintBrush, dpi);
        }
    }

    private void DrawCar(DrawingContext dc)
    {
        if (!HasCurrent)
        {
            return;
        }

        var p = State.Viewport.ToScreen(CurrentX, CurrentZ);
        dc.DrawEllipse(GlowBrush, null, p, 20, 20);
        dc.DrawEllipse(ChartPalette.TextBrush, DotRing, p, 4.5, 4.5);
    }

    private void DrawLegend(DrawingContext dc, double dpi, double height)
    {
        const double barWidth = 150;
        var box = new Rect(14, height - 62, barWidth + 28 + 128, 50);
        dc.DrawRoundedRectangle(LegendBackground, LegendBorder, box, 10, 10);
        _text.Draw(dc, string.Format(CultureInfo.CurrentCulture, InsightsStrings.MapLegendTitleFormat, SpeedUnit), box.X + 14, box.Y + 7, 11, ChartPalette.MutedBrush, dpi, bold: true);
        dc.DrawRoundedRectangle(LegendRamp, null, new Rect(box.X + 14, box.Y + 26, barWidth, 5), 2.5, 2.5);

        var scale = SpeedScale;
        _text.Draw(dc, (MinSpeedMs * scale).ToString("0", CultureInfo.CurrentCulture), box.X + 14, box.Y + 32, 11, ChartPalette.TextBrush, dpi, numeral: true);
        _text.DrawRight(dc, (MaxSpeedMs * scale).ToString("0", CultureInfo.CurrentCulture), box.X + 14 + barWidth, box.Y + 32, 11, ChartPalette.TextBrush, dpi, numeral: true);
        _text.DrawCentered(dc, ((MinSpeedMs + MaxSpeedMs) / 2 * scale).ToString("0", CultureInfo.CurrentCulture), box.X + 14 + barWidth / 2, box.Y + 32, 11, ChartPalette.MutedBrush, dpi, numeral: true);

        // Braking marker key, to the right of the ramp.
        var keyX = box.X + 14 + barWidth + 22;
        dc.DrawEllipse(ChartPalette.BrakeBrush, MarkerRing, new Point(keyX + 4, box.Y + 33), 4, 4);
        _text.Draw(dc, InsightsStrings.MapBrakingLegend, keyX + 14, box.Y + 25, 11, ChartPalette.MutedBrush, dpi);
    }

    private void Rebuild(ReadOnlySpan<TrackPoint> points, double width, double height)
    {
        _dirty = false;
        _builtFor = new Size(width, height);
        Array.Clear(_bucketGeometry);
        _brakeMarkers.Clear();
        _casing = null;
        if (points.IsEmpty)
        {
            return;
        }

        // Auto-fit unless the user took over the view.
        var view = State;
        if (!view.Manual || !view.HasUsableView)
        {
            double minX = double.MaxValue, maxX = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
            foreach (var p in points)
            {
                minX = Math.Min(minX, p.X);
                maxX = Math.Max(maxX, p.X);
                minZ = Math.Min(minZ, p.Z);
                maxZ = Math.Max(maxZ, p.Z);
            }

            if (HasCurrent)
            {
                minX = Math.Min(minX, CurrentX);
                maxX = Math.Max(maxX, CurrentX);
                minZ = Math.Min(minZ, CurrentZ);
                maxZ = Math.Max(maxZ, CurrentZ);
            }

            view.Viewport = MapViewport.Fit(minX, maxX, minZ, maxZ, width, height, Padding);
            view.FitScale = view.Viewport.Scale;
            view.Manual = false;
        }

        var casing = new StreamGeometry();
        var contexts = new StreamGeometryContext?[Buckets];
        var geometries = new StreamGeometry?[Buckets];
        var lastEnd = new int[Buckets];
        Array.Fill(lastEnd, -1);

        var minSpeed = MinSpeedMs;
        var maxSpeed = MaxSpeedMs;
        var bounds = new Rect(-Cull, -Cull, width + 2 * Cull, height + 2 * Cull);

        using (var casingContext = casing.Open())
        {
            var casingOpen = false;
            var previous = State.Viewport.ToScreen(points[0].X, points[0].Z);
            AddMarkerIfZoneStart(points, 0, previous, bounds);

            for (var i = 1; i < points.Length; i++)
            {
                var current = State.Viewport.ToScreen(points[i].X, points[i].Z);
                AddMarkerIfZoneStart(points, i, current, bounds);

                if (points[i].StartsSegment || (!bounds.Contains(previous) && !bounds.Contains(current)))
                {
                    // Jump, or entirely off screen: draw nothing and make the next segment start a fresh figure.
                    casingOpen = false;
                    Array.Fill(lastEnd, -1);
                    previous = current;
                    continue;
                }

                if (!casingOpen)
                {
                    casingContext.BeginFigure(previous, isFilled: false, isClosed: false);
                    casingOpen = true;
                }

                casingContext.LineTo(current, isStroked: true, isSmoothJoin: true);

                var t = SpeedRamp.Normalize((points[i].SpeedMs + points[i - 1].SpeedMs) / 2, minSpeed, maxSpeed);
                var bucket = SpeedRamp.Bucket(t, Buckets);
                if (contexts[bucket] is null)
                {
                    geometries[bucket] = new StreamGeometry();
                    contexts[bucket] = geometries[bucket]!.Open();
                }

                var context = contexts[bucket]!;
                if (lastEnd[bucket] != i - 1)
                {
                    context.BeginFigure(previous, isFilled: false, isClosed: false);
                }

                context.LineTo(current, isStroked: true, isSmoothJoin: true);
                lastEnd[bucket] = i;
                previous = current;
            }
        }

        casing.Freeze();
        _casing = casing;
        for (var i = 0; i < Buckets; i++)
        {
            if (contexts[i] is { } context)
            {
                context.Close();
                geometries[i]!.Freeze();
                _bucketGeometry[i] = geometries[i];
            }
        }
    }

    private void AddMarkerIfZoneStart(ReadOnlySpan<TrackPoint> points, int i, Point screen, Rect bounds)
    {
        var p = points[i];
        if (p.Braking && (i == 0 || p.StartsSegment || !points[i - 1].Braking) && bounds.Contains(screen))
        {
            _brakeMarkers.Add(screen);
        }
    }
}
