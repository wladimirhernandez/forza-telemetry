namespace ForzaTelemetry.Core.Insights;

/// <summary>Handling balance classification.</summary>
public enum BalanceState
{
    /// <summary>Not enough slip to judge (driving straight or gently).</summary>
    Unknown,

    Neutral,

    /// <summary>Front tires slide more than the rears: the car pushes wide.</summary>
    Understeer,

    /// <summary>Rear tires slide more than the fronts: the tail steps out.</summary>
    Oversteer,
}

/// <summary>Result of <see cref="BalanceAnalyzer.AnalyzeBalance(float, float, float, float)"/>.</summary>
/// <param name="Index">-1 (pure oversteer) .. 0 (neutral) .. +1 (pure understeer).</param>
/// <param name="State">Classification of <paramref name="Index"/>.</param>
public readonly record struct BalanceReading(float Index, BalanceState State);

/// <summary>Plain advice keys for tire temperature balance. The app maps each to a localized sentence.</summary>
[Flags]
public enum TireAdvice
{
    None = 0,

    /// <summary>Front axle clearly hotter than the rear: front is working too hard (understeer-prone).</summary>
    FrontHotterThanRear = 1,

    /// <summary>Rear axle clearly hotter than the front.</summary>
    RearHotterThanFront = 2,

    /// <summary>Left side clearly hotter than the right.</summary>
    LeftHotterThanRight = 4,

    /// <summary>Right side clearly hotter than the left.</summary>
    RightHotterThanLeft = 8,

    /// <summary>Every tire below the working range.</summary>
    AllTooCold = 16,

    /// <summary>Every tire above the working range.</summary>
    AllTooHot = 32,
}

/// <summary>
/// Pure functions that turn raw tire/suspension numbers into handling insights.
/// </summary>
/// <remarks>
/// All thresholds are INITIAL GUESSES chosen to be conservative; they are exposed as constants so they can be
/// tuned once real telemetry is available. Every method is a stateless function of its arguments, so it is
/// trivially thread-safe. Stateful event counting lives in <see cref="SuspensionBottomingCounter"/>.
/// </remarks>
public static class BalanceAnalyzer
{
    /// <summary>Below this combined front+rear average slip the balance is not meaningful.</summary>
    public const float MinTotalSlip = 0.1f;

    /// <summary>|index| above this is classified as under/oversteer (initial guess).</summary>
    public const float BalanceThreshold = 0.15f;

    /// <summary>Normalized suspension travel at or above this counts as bottoming out (initial guess).</summary>
    public const float BottomingTravel = 0.98f;

    /// <summary>Axle/side average temperature difference (wire unit, assumed Fahrenheit) that triggers advice.</summary>
    public const float TempSpreadThreshold = 18f;

    /// <summary>Average tire temperature (wire unit, assumed Fahrenheit) below which tires are cold (~60 C).</summary>
    public const float ColdTemp = 140f;

    /// <summary>Average tire temperature (wire unit, assumed Fahrenheit) above which tires are overheated (~110 C).</summary>
    public const float HotTemp = 230f;

    /// <summary>
    /// Understeer/oversteer index from the average ABSOLUTE slip angle of the front and rear axles.
    /// </summary>
    /// <remarks>
    /// Slip angle is how far a tire's heading differs from where it travels; the axle that slides more is
    /// the one that has lost grip. The index <c>(front - rear) / (front + rear)</c> is bounded to [-1, 1] and
    /// independent of how hard the corner is, so one threshold works at any speed. Positive = understeer.
    /// Absolute values are used because left/right tires have opposite signs in a corner.
    /// </remarks>
    public static BalanceReading AnalyzeBalance(
        float slipFrontLeft, float slipFrontRight, float slipRearLeft, float slipRearRight)
    {
        var front = (MathF.Abs(slipFrontLeft) + MathF.Abs(slipFrontRight)) / 2f;
        var rear = (MathF.Abs(slipRearLeft) + MathF.Abs(slipRearRight)) / 2f;
        var total = front + rear;

        if (!float.IsFinite(total) || total < MinTotalSlip)
        {
            return new BalanceReading(0f, BalanceState.Unknown);
        }

        var index = (front - rear) / total;
        var state = index >= BalanceThreshold
            ? BalanceState.Understeer
            : index <= -BalanceThreshold ? BalanceState.Oversteer : BalanceState.Neutral;
        return new BalanceReading(index, state);
    }

    /// <summary>Balance for a packet.</summary>
    public static BalanceReading AnalyzeBalance(TelemetryPacket p) => AnalyzeBalance(
        p.TireSlipAngleFrontLeft, p.TireSlipAngleFrontRight, p.TireSlipAngleRearLeft, p.TireSlipAngleRearRight);

    /// <summary>Balance for a history sample.</summary>
    public static BalanceReading AnalyzeBalance(in TelemetrySample s) => AnalyzeBalance(
        s.SlipAngleFrontLeft, s.SlipAngleFrontRight, s.SlipAngleRearLeft, s.SlipAngleRearRight);

    /// <summary>True when a normalized suspension travel value is at the bump stop.</summary>
    public static bool IsBottomedOut(float normalizedTravel) => normalizedTravel >= BottomingTravel;

    /// <summary>
    /// Tire temperature advice: compares the front vs rear and left vs right averages, and flags a
    /// uniformly cold or overheated set. Several flags can be returned together.
    /// </summary>
    public static TireAdvice AnalyzeTireTemperatures(
        float frontLeft, float frontRight, float rearLeft, float rearRight)
    {
        if (!float.IsFinite(frontLeft + frontRight + rearLeft + rearRight))
        {
            return TireAdvice.None;
        }

        var advice = TireAdvice.None;
        var front = (frontLeft + frontRight) / 2f;
        var rear = (rearLeft + rearRight) / 2f;
        var left = (frontLeft + rearLeft) / 2f;
        var right = (frontRight + rearRight) / 2f;

        if (front - rear >= TempSpreadThreshold)
        {
            advice |= TireAdvice.FrontHotterThanRear;
        }
        else if (rear - front >= TempSpreadThreshold)
        {
            advice |= TireAdvice.RearHotterThanFront;
        }

        if (left - right >= TempSpreadThreshold)
        {
            advice |= TireAdvice.LeftHotterThanRight;
        }
        else if (right - left >= TempSpreadThreshold)
        {
            advice |= TireAdvice.RightHotterThanLeft;
        }

        var maxTemp = Math.Max(Math.Max(frontLeft, frontRight), Math.Max(rearLeft, rearRight));
        var minTemp = Math.Min(Math.Min(frontLeft, frontRight), Math.Min(rearLeft, rearRight));
        if (maxTemp < ColdTemp)
        {
            advice |= TireAdvice.AllTooCold;
        }
        else if (minTemp > HotTemp)
        {
            advice |= TireAdvice.AllTooHot;
        }

        return advice;
    }

    /// <summary>Tire temperature advice for a packet.</summary>
    public static TireAdvice AnalyzeTireTemperatures(TelemetryPacket p) => AnalyzeTireTemperatures(
        p.TireTempFrontLeft, p.TireTempFrontRight, p.TireTempRearLeft, p.TireTempRearRight);
}
