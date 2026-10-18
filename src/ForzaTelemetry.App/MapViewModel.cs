using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForzaTelemetry.App.Controls;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core.Insights;

namespace ForzaTelemetry.App;

/// <summary>
/// The Map screen: the driven path, its session statistics and a Clear command.
/// </summary>
/// <remarks>
/// The path can hold up to 20,000 points (about 400 KB), so copying it on every 20 Hz tick would be wasteful. The
/// view model remembers <see cref="TrackPath.Version"/> and copies only when it changed; the current-position dot is a
/// separate, much cheaper update (one sample) that happens every refresh.
/// </remarks>
public sealed partial class MapViewModel : ObservableObject
{
    private const double MetersPerKilometer = 1000;
    private const double MetersPerMile = 1609.344;

    private readonly TrackPath _path;
    private readonly TelemetryHistory _history;
    private readonly UnitsService _units;
    private readonly TelemetrySample[] _latest = new TelemetrySample[1];
    private int _lastVersion = -1;

    /// <param name="path">The session path fed per packet.</param>
    /// <param name="history">Used only for the car's current position.</param>
    /// <param name="units">Speed unit and its label (the distance follows it: mph means miles).</param>
    public MapViewModel(TrackPath path, TelemetryHistory history, UnitsService units)
    {
        _path = path;
        _history = history;
        _units = units;
        _points = new TrackPoint[256];
        _speedUnitLabel = units.SpeedLabel;
        _speedScale = units.ConvertSpeed(3.6);
    }

    /// <summary>Copy of the path the map draws; only the first <see cref="PointCount"/> entries are valid.</summary>
    [ObservableProperty]
    private TrackPoint[] _points;

    [ObservableProperty]
    private int _pointCount;

    /// <summary>Changes when the path changed (the map's rebuild signal).</summary>
    [ObservableProperty]
    private int _dataVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasData;

    public bool IsEmpty => !HasData;

    [ObservableProperty]
    private double _minSpeedMs;

    [ObservableProperty]
    private double _maxSpeedMs;

    [ObservableProperty]
    private double _currentX;

    [ObservableProperty]
    private double _currentZ;

    [ObservableProperty]
    private bool _hasCurrent;

    [ObservableProperty]
    private double _speedScale = 3.6;

    [ObservableProperty]
    private string _speedUnitLabel;

    [ObservableProperty]
    private string _distanceText = FormatDistance(0, SpeedUnit.Kmh);

    [ObservableProperty]
    private string _topSpeedText = "0";

    [ObservableProperty]
    private int _brakingZones;

    /// <summary>Pulls the path (when it changed) and the car position. Called by the shell while this page is showing.</summary>
    public void Refresh()
    {
        SpeedUnitLabel = _units.SpeedLabel;
        SpeedScale = _units.ConvertSpeed(3.6);

        if (_history.CopyTo(_latest) == 1)
        {
            CurrentX = _latest[0].PositionX;
            CurrentZ = _latest[0].PositionZ;
            HasCurrent = true;
        }

        var version = _path.Version;
        if (version != _lastVersion)
        {
            _lastVersion = version;
            CopyPath();
        }

        // Distance and top speed follow the unit setting even when the path itself did not change.
        DistanceText = FormatDistance(_distanceMeters, _units.SpeedUnit);
        TopSpeedText = _units.FormatSpeed(MaxSpeedMs * 3.6) + " " + _units.SpeedLabel;
    }

    private double _distanceMeters;

    private void CopyPath()
    {
        var needed = _path.Count;
        if (needed > Points.Length)
        {
            // Grow geometrically (x2) so a path that keeps growing allocates O(log n) times, not every refresh.
            Points = new TrackPoint[Math.Max(needed, Points.Length * 2)];
        }

        var copied = _path.CopyTo(Points);
        var stats = TrackStats.Compute(Points.AsSpan(0, copied));
        PointCount = copied;
        MinSpeedMs = stats.MinSpeedMs;
        MaxSpeedMs = stats.MaxSpeedMs;
        BrakingZones = stats.BrakingZones;
        _distanceMeters = stats.DistanceMeters;
        HasData = copied >= 2;
        DataVersion++;
    }

    [RelayCommand]
    private void Clear()
    {
        _path.Clear();
        _lastVersion = -1;
        Refresh();
    }

    /// <summary>The distance in kilometers, or in miles when the speed unit is mph, with a unit suffix.</summary>
    public static string FormatDistance(double meters, SpeedUnit unit)
    {
        var useMiles = unit == SpeedUnit.Mph;
        var value = meters / (useMiles ? MetersPerMile : MetersPerKilometer);
        var text = value.ToString(value < 10 ? "0.00" : "0.0", CultureInfo.CurrentCulture);
        return text + " " + (useMiles ? InsightsStrings.DistanceMi : InsightsStrings.DistanceKm);
    }
}
