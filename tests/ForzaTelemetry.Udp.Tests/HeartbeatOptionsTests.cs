namespace ForzaTelemetry.Udp.Tests;

/// <summary>
/// Covers <see cref="HeartbeatOptions"/>'s constructor validation (T5 review finding #2): both
/// thresholds must be strictly positive and within <see cref="HeartbeatOptions.MaxThreshold"/>.
/// </summary>
public sealed class HeartbeatOptionsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveInitialSilence_ThrowsArgumentOutOfRangeException(int seconds)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HeartbeatOptions(
                InitialSilence: TimeSpan.FromSeconds(seconds),
                RepeatInterval: TimeSpan.FromSeconds(10)));

        Assert.Equal("InitialSilence", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveRepeatInterval_ThrowsArgumentOutOfRangeException(int seconds)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HeartbeatOptions(
                InitialSilence: TimeSpan.FromSeconds(5),
                RepeatInterval: TimeSpan.FromSeconds(seconds)));

        Assert.Equal("RepeatInterval", ex.ParamName);
    }

    [Fact]
    public void Constructor_InitialSilenceAboveMaxThreshold_ThrowsArgumentOutOfRangeException()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HeartbeatOptions(
                InitialSilence: HeartbeatOptions.MaxThreshold + TimeSpan.FromSeconds(1),
                RepeatInterval: TimeSpan.FromSeconds(10)));

        Assert.Equal("InitialSilence", ex.ParamName);
    }

    [Fact]
    public void Constructor_RepeatIntervalAboveMaxThreshold_ThrowsArgumentOutOfRangeException()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HeartbeatOptions(
                InitialSilence: TimeSpan.FromSeconds(5),
                RepeatInterval: HeartbeatOptions.MaxThreshold + TimeSpan.FromSeconds(1)));

        Assert.Equal("RepeatInterval", ex.ParamName);
    }

    [Fact]
    public void Constructor_ValueAtExactlyMaxThreshold_IsAccepted()
    {
        // The boundary itself must be valid, not just "anything strictly below it".
        var options = new HeartbeatOptions(
            InitialSilence: HeartbeatOptions.MaxThreshold,
            RepeatInterval: HeartbeatOptions.MaxThreshold);

        Assert.Equal(HeartbeatOptions.MaxThreshold, options.InitialSilence);
        Assert.Equal(HeartbeatOptions.MaxThreshold, options.RepeatInterval);
    }

    [Fact]
    public void Default_IsWithinValidRange()
    {
        // Guards against a future edit accidentally moving Default outside its own validation.
        Assert.True(HeartbeatOptions.Default.InitialSilence > TimeSpan.Zero);
        Assert.True(HeartbeatOptions.Default.RepeatInterval > TimeSpan.Zero);
        Assert.True(HeartbeatOptions.Default.InitialSilence <= HeartbeatOptions.MaxThreshold);
        Assert.True(HeartbeatOptions.Default.RepeatInterval <= HeartbeatOptions.MaxThreshold);
    }
}
