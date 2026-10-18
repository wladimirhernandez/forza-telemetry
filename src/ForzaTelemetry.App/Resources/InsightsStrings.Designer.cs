//------------------------------------------------------------------------------
// Hand-maintained strongly typed accessor for InsightsStrings.resx (same shape as the generated Strings.cs).
// It is a separate resource set so the Analysis/Map/Tuning pages do not touch Strings.resx. Add a property
// here whenever a key is added to InsightsStrings.resx (and to InsightsStrings.es.resx).
//------------------------------------------------------------------------------

#nullable enable

namespace ForzaTelemetry.App.Resources;

/// <summary>Localized text of the Analysis, Map and Tuning pages (neutral English; Spanish in InsightsStrings.es.resx).</summary>
public static class InsightsStrings
{
    private static global::System.Resources.ResourceManager? _resourceManager;

    /// <summary>The resource manager for <c>InsightsStrings.resx</c> and its satellite cultures.</summary>
    public static global::System.Resources.ResourceManager ResourceManager =>
        _resourceManager ??= new global::System.Resources.ResourceManager(
            "ForzaTelemetry.App.Resources.InsightsStrings", typeof(InsightsStrings).Assembly);

    /// <summary>Culture override for lookups; null uses the current UI culture (as set by LanguageService).</summary>
    public static global::System.Globalization.CultureInfo? Culture { get; set; }

    /// <summary>"Live traces"</summary>
    public static string AnalysisTitle => ResourceManager.GetString("AnalysisTitle", Culture)!;

    /// <summary>"Window"</summary>
    public static string AnalysisWindowLabel => ResourceManager.GetString("AnalysisWindowLabel", Culture)!;

    /// <summary>"{0} s"</summary>
    public static string WindowSecondsFormat => ResourceManager.GetString("WindowSecondsFormat", Culture)!;

    /// <summary>"Drive to see live traces"</summary>
    public static string AnalysisEmptyTitle => ResourceManager.GetString("AnalysisEmptyTitle", Culture)!;

    /// <summary>"Speed, RPM, pedals, steering and gear appear here as soon as the race is on."</summary>
    public static string AnalysisEmptyHint => ResourceManager.GetString("AnalysisEmptyHint", Culture)!;

    /// <summary>"Speed"</summary>
    public static string LaneSpeed => ResourceManager.GetString("LaneSpeed", Culture)!;

    /// <summary>"RPM"</summary>
    public static string LaneRpm => ResourceManager.GetString("LaneRpm", Culture)!;

    /// <summary>"Pedals"</summary>
    public static string LanePedals => ResourceManager.GetString("LanePedals", Culture)!;

    /// <summary>"Steering"</summary>
    public static string LaneSteering => ResourceManager.GetString("LaneSteering", Culture)!;

    /// <summary>"Gear"</summary>
    public static string LaneGear => ResourceManager.GetString("LaneGear", Culture)!;

    /// <summary>"Throttle"</summary>
    public static string ThrottleLabel => ResourceManager.GetString("ThrottleLabel", Culture)!;

    /// <summary>"Brake"</summary>
    public static string BrakeLabel => ResourceManager.GetString("BrakeLabel", Culture)!;

    /// <summary>"L"</summary>
    public static string SteerLeftShort => ResourceManager.GetString("SteerLeftShort", Culture)!;

    /// <summary>"R"</summary>
    public static string SteerRightShort => ResourceManager.GetString("SteerRightShort", Culture)!;

    /// <summary>"{0:0.0} s ago"</summary>
    public static string SecondsAgoFormat => ResourceManager.GetString("SecondsAgoFormat", Culture)!;

    /// <summary>"now"</summary>
    public static string TimeNow => ResourceManager.GetString("TimeNow", Culture)!;

    /// <summary>"Live telemetry traces"</summary>
    public static string AnalysisChartName => ResourceManager.GetString("AnalysisChartName", Culture)!;

    /// <summary>"Track map"</summary>
    public static string MapTitle => ResourceManager.GetString("MapTitle", Culture)!;

    /// <summary>"Drive to draw your path"</summary>
    public static string MapEmptyTitle => ResourceManager.GetString("MapEmptyTitle", Culture)!;

    /// <summary>"The route you drive is drawn live, colored by speed."</summary>
    public static string MapEmptyHint => ResourceManager.GetString("MapEmptyHint", Culture)!;

    /// <summary>"Clear"</summary>
    public static string MapClear => ResourceManager.GetString("MapClear", Culture)!;

    /// <summary>"Clear the drawn path"</summary>
    public static string MapClearTooltip => ResourceManager.GetString("MapClearTooltip", Culture)!;

    /// <summary>"Distance"</summary>
    public static string MapDistance => ResourceManager.GetString("MapDistance", Culture)!;

    /// <summary>"Top speed"</summary>
    public static string MapTopSpeed => ResourceManager.GetString("MapTopSpeed", Culture)!;

    /// <summary>"Braking zones"</summary>
    public static string MapBrakingZones => ResourceManager.GetString("MapBrakingZones", Culture)!;

    /// <summary>"Speed"</summary>
    public static string MapLegendTitle => ResourceManager.GetString("MapLegendTitle", Culture)!;

    /// <summary>"Braking point"</summary>
    public static string MapBrakingLegend => ResourceManager.GetString("MapBrakingLegend", Culture)!;

    /// <summary>"Scroll to zoom, drag to pan, double-click to reset"</summary>
    public static string MapNavigationHint => ResourceManager.GetString("MapNavigationHint", Culture)!;

    /// <summary>"km"</summary>
    public static string DistanceKm => ResourceManager.GetString("DistanceKm", Culture)!;

    /// <summary>"mi"</summary>
    public static string DistanceMi => ResourceManager.GetString("DistanceMi", Culture)!;

    /// <summary>"Track map colored by speed"</summary>
    public static string MapChartName => ResourceManager.GetString("MapChartName", Culture)!;

    /// <summary>"FL"</summary>
    public static string CornerFrontLeft => ResourceManager.GetString("CornerFrontLeft", Culture)!;

    /// <summary>"FR"</summary>
    public static string CornerFrontRight => ResourceManager.GetString("CornerFrontRight", Culture)!;

    /// <summary>"RL"</summary>
    public static string CornerRearLeft => ResourceManager.GetString("CornerRearLeft", Culture)!;

    /// <summary>"RR"</summary>
    public static string CornerRearRight => ResourceManager.GetString("CornerRearRight", Culture)!;

    /// <summary>"Power and torque"</summary>
    public static string DynoTitle => ResourceManager.GetString("DynoTitle", Culture)!;

    /// <summary>"Full-throttle pulls build this curve."</summary>
    public static string DynoHint => ResourceManager.GetString("DynoHint", Culture)!;

    /// <summary>"No curve yet"</summary>
    public static string DynoEmptyTitle => ResourceManager.GetString("DynoEmptyTitle", Culture)!;

    /// <summary>"Accelerate at full throttle through the rev range in one gear to capture power and torque."</summary>
    public static string DynoEmptyHint => ResourceManager.GetString("DynoEmptyHint", Culture)!;

    /// <summary>"Reset curve"</summary>
    public static string DynoReset => ResourceManager.GetString("DynoReset", Culture)!;

    /// <summary>"Forget the curve captured for this car"</summary>
    public static string DynoResetTooltip => ResourceManager.GetString("DynoResetTooltip", Culture)!;

    /// <summary>"Peak power"</summary>
    public static string DynoPeakPower => ResourceManager.GetString("DynoPeakPower", Culture)!;

    /// <summary>"Peak torque"</summary>
    public static string DynoPeakTorque => ResourceManager.GetString("DynoPeakTorque", Culture)!;

    /// <summary>"kW"</summary>
    public static string DynoPowerAxis => ResourceManager.GetString("DynoPowerAxis", Culture)!;

    /// <summary>"N·m"</summary>
    public static string DynoTorqueAxis => ResourceManager.GetString("DynoTorqueAxis", Culture)!;

    /// <summary>"rpm"</summary>
    public static string DynoRpmAxis => ResourceManager.GetString("DynoRpmAxis", Culture)!;

    /// <summary>"{0} kW · {1} hp @ {2} rpm"</summary>
    public static string DynoPeakPowerFormat => ResourceManager.GetString("DynoPeakPowerFormat", Culture)!;

    /// <summary>"{0} N·m @ {1} rpm"</summary>
    public static string DynoPeakTorqueFormat => ResourceManager.GetString("DynoPeakTorqueFormat", Culture)!;

    /// <summary>"Power and torque curve"</summary>
    public static string DynoChartName => ResourceManager.GetString("DynoChartName", Culture)!;

    /// <summary>"Car {0} · PI {1}"</summary>
    public static string CarLabelFormat => ResourceManager.GetString("CarLabelFormat", Culture)!;

    /// <summary>"No car detected yet"</summary>
    public static string NoCarYet => ResourceManager.GetString("NoCarYet", Culture)!;

    /// <summary>"Handling balance"</summary>
    public static string BalanceTitle => ResourceManager.GetString("BalanceTitle", Culture)!;

    /// <summary>"Understeer"</summary>
    public static string BalanceUndersteer => ResourceManager.GetString("BalanceUndersteer", Culture)!;

    /// <summary>"Oversteer"</summary>
    public static string BalanceOversteer => ResourceManager.GetString("BalanceOversteer", Culture)!;

    /// <summary>"Neutral"</summary>
    public static string BalanceNeutral => ResourceManager.GetString("BalanceNeutral", Culture)!;

    /// <summary>"Take a corner to read the balance."</summary>
    public static string BalanceUnknown => ResourceManager.GetString("BalanceUnknown", Culture)!;

    /// <summary>"The front slides more than the rear. Try a softer front anti-roll bar, a stiffer rear one, or more front downforce."</summary>
    public static string BalanceAdviceUndersteer => ResourceManager.GetString("BalanceAdviceUndersteer", Culture)!;

    /// <summary>"The rear slides more than the front. Try a stiffer front anti-roll bar, a softer rear one, or more rear downforce."</summary>
    public static string BalanceAdviceOversteer => ResourceManager.GetString("BalanceAdviceOversteer", Culture)!;

    /// <summary>"Well balanced: both axles grip about the same."</summary>
    public static string BalanceAdviceNeutral => ResourceManager.GetString("BalanceAdviceNeutral", Culture)!;

    /// <summary>"Understeer and oversteer balance"</summary>
    public static string BalanceBarName => ResourceManager.GetString("BalanceBarName", Culture)!;

    /// <summary>"Suspension"</summary>
    public static string SuspensionTitle => ResourceManager.GetString("SuspensionTitle", Culture)!;

    /// <summary>"Times each corner hit the bump stop."</summary>
    public static string SuspensionHint => ResourceManager.GetString("SuspensionHint", Culture)!;

    /// <summary>"Reset counters"</summary>
    public static string SuspensionReset => ResourceManager.GetString("SuspensionReset", Culture)!;

    /// <summary>"Set the bottoming counters back to zero"</summary>
    public static string SuspensionResetTooltip => ResourceManager.GetString("SuspensionResetTooltip", Culture)!;

    /// <summary>"No bottoming so far."</summary>
    public static string SuspensionAdviceNone => ResourceManager.GetString("SuspensionAdviceNone", Culture)!;

    /// <summary>"Occasional bottoming is normal after big landings."</summary>
    public static string SuspensionAdviceFew => ResourceManager.GetString("SuspensionAdviceFew", Culture)!;

    /// <summary>"The front end bottoms out often. Try stiffer front springs or more ride height."</summary>
    public static string SuspensionAdviceFront => ResourceManager.GetString("SuspensionAdviceFront", Culture)!;

    /// <summary>"The rear end bottoms out often. Try stiffer rear springs or more ride height."</summary>
    public static string SuspensionAdviceRear => ResourceManager.GetString("SuspensionAdviceRear", Culture)!;

    /// <summary>"Both axles bottom out often. Try stiffer springs and more ride height."</summary>
    public static string SuspensionAdviceBoth => ResourceManager.GetString("SuspensionAdviceBoth", Culture)!;

    /// <summary>"Tires"</summary>
    public static string TiresTitle => ResourceManager.GetString("TiresTitle", Culture)!;

    /// <summary>"Tire temperatures look balanced."</summary>
    public static string TiresAdviceBalanced => ResourceManager.GetString("TiresAdviceBalanced", Culture)!;

    /// <summary>"Waiting for tire data."</summary>
    public static string TiresNoData => ResourceManager.GetString("TiresNoData", Culture)!;

    /// <summary>"The front tires run noticeably hotter than the rear: the front axle does most of the work and tends to understeer. Soften the front anti-roll bar or add rear grip."</summary>
    public static string TiresAdviceFrontHotter => ResourceManager.GetString("TiresAdviceFrontHotter", Culture)!;

    /// <summary>"The rear tires run noticeably hotter than the front: the rear axle does most of the work and tends to oversteer. Soften the rear anti-roll bar or add front grip."</summary>
    public static string TiresAdviceRearHotter => ResourceManager.GetString("TiresAdviceRearHotter", Culture)!;

    /// <summary>"The left tires are hotter than the right. That is normal on tracks with mostly right-hand corners; otherwise check camber and pressures side to side."</summary>
    public static string TiresAdviceLeftHotter => ResourceManager.GetString("TiresAdviceLeftHotter", Culture)!;

    /// <summary>"The right tires are hotter than the left. That is normal on tracks with mostly left-hand corners; otherwise check camber and pressures side to side."</summary>
    public static string TiresAdviceRightHotter => ResourceManager.GetString("TiresAdviceRightHotter", Culture)!;

    /// <summary>"All tires are below their working range. Drive harder for a few corners to bring them up to temperature."</summary>
    public static string TiresAdviceAllCold => ResourceManager.GetString("TiresAdviceAllCold", Culture)!;

    /// <summary>"All tires are overheating. Ease off for a moment and check the tire pressures."</summary>
    public static string TiresAdviceAllHot => ResourceManager.GetString("TiresAdviceAllHot", Culture)!;

    /// <summary>"Drive to see tuning insights."</summary>
    public static string TuningEmptyHint => ResourceManager.GetString("TuningEmptyHint", Culture)!;
}
