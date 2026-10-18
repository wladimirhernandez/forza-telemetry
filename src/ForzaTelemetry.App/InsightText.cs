using ForzaTelemetry.App.Resources;
using ForzaTelemetry.Core.Insights;

namespace ForzaTelemetry.App;

/// <summary>
/// Turns the analyzers' language-neutral results (flags, enums, counts) into the localized sentences the Tuning page shows.
/// </summary>
/// <remarks>
/// The Core analyzers deliberately return keys (<see cref="TireAdvice"/> flags, <see cref="BalanceState"/>) and never text,
/// so the wording and its translation live in the app, in <c>InsightsStrings.resx</c>. This class is the one mapping from
/// key to sentence: pure functions, no state, so a test can check every key without a window.
/// </remarks>
public static class InsightText
{
    /// <summary>Bottoming events at which a corner is shown as "frequent" (amber) instead of "occasional".</summary>
    public const int FrequentBottoming = 3;

    /// <summary>The state label shown under the balance bar.</summary>
    public static string BalanceLabel(BalanceState state) => state switch
    {
        BalanceState.Understeer => InsightsStrings.BalanceUndersteer,
        BalanceState.Oversteer => InsightsStrings.BalanceOversteer,
        BalanceState.Neutral => InsightsStrings.BalanceNeutral,
        _ => string.Empty
    };

    /// <summary>The plain-language tip for a balance state; <see cref="BalanceState.Unknown"/> asks the driver to take a corner.</summary>
    public static string BalanceAdvice(BalanceState state) => state switch
    {
        BalanceState.Understeer => InsightsStrings.BalanceAdviceUndersteer,
        BalanceState.Oversteer => InsightsStrings.BalanceAdviceOversteer,
        BalanceState.Neutral => InsightsStrings.BalanceAdviceNeutral,
        _ => InsightsStrings.BalanceUnknown
    };

    /// <summary>Classifies a (smoothed) balance index with the analyzer's own threshold, so bar and label always agree.</summary>
    public static BalanceState ClassifyBalance(double index) =>
        index >= BalanceAnalyzer.BalanceThreshold ? BalanceState.Understeer
        : index <= -BalanceAnalyzer.BalanceThreshold ? BalanceState.Oversteer
        : BalanceState.Neutral;

    /// <summary>One sentence per set flag, in a stable order (axle, side, overall); a single "balanced" sentence when none is set.</summary>
    public static IReadOnlyList<string> TireLines(TireAdvice advice)
    {
        var lines = new List<string>();
        Add(Has(advice, TireAdvice.FrontHotterThanRear), InsightsStrings.TiresAdviceFrontHotter);
        Add(Has(advice, TireAdvice.RearHotterThanFront), InsightsStrings.TiresAdviceRearHotter);
        Add(Has(advice, TireAdvice.LeftHotterThanRight), InsightsStrings.TiresAdviceLeftHotter);
        Add(Has(advice, TireAdvice.RightHotterThanLeft), InsightsStrings.TiresAdviceRightHotter);
        Add(Has(advice, TireAdvice.AllTooCold), InsightsStrings.TiresAdviceAllCold);
        Add(Has(advice, TireAdvice.AllTooHot), InsightsStrings.TiresAdviceAllHot);
        if (lines.Count == 0)
        {
            lines.Add(InsightsStrings.TiresAdviceBalanced);
        }

        return lines;

        void Add(bool present, string text)
        {
            if (present)
            {
                lines.Add(text);
            }
        }
    }

    private static bool Has(TireAdvice advice, TireAdvice flag) => (advice & flag) == flag;

    /// <summary>0 = never, 1 = occasional, 2 = frequent: drives the color of a corner's counter.</summary>
    public static int BottomingSeverity(int count) => count <= 0 ? 0 : count < FrequentBottoming ? 1 : 2;

    /// <summary>
    /// The suspension tip: nothing yet, a calming note for a few hits, or which axle bottoms out. It uses the SAME
    /// per-corner rule as <see cref="BottomingSeverity"/>: a corner is "frequent" at <see cref="FrequentBottoming"/>
    /// hits, an axle is the problem when either of its corners is frequent, and the tip can therefore never contradict
    /// the colors of the counters next to it.
    /// </summary>
    public static string SuspensionAdvice(BottomingCounts counts)
    {
        if (counts.Total == 0)
        {
            return InsightsStrings.SuspensionAdviceNone;
        }

        var frontFrequent = BottomingSeverity(counts.FrontLeft) == 2 || BottomingSeverity(counts.FrontRight) == 2;
        var rearFrequent = BottomingSeverity(counts.RearLeft) == 2 || BottomingSeverity(counts.RearRight) == 2;
        return (frontFrequent, rearFrequent) switch
        {
            (true, true) => InsightsStrings.SuspensionAdviceBoth,
            (true, false) => InsightsStrings.SuspensionAdviceFront,
            (false, true) => InsightsStrings.SuspensionAdviceRear,
            _ => InsightsStrings.SuspensionAdviceFew
        };
    }
}
