namespace ForzaTelemetry.Core.Tests;

public class TelemetryParserTests
{
    // ---- Wrong-size buffers -------------------------------------------------
    // The parser must never silently misread a truncated or oversized datagram; a UDP receive
    // loop can get garbage from other traffic on the same port, so size validation is the first
    // line of defense.

    [Theory]
    [InlineData(0)]
    [InlineData(323)]
    [InlineData(325)]
    [InlineData(331)] // the old FM7/FH4 "dash" packet size — a classic way to feed the wrong game's packet in
    public void TryParse_returns_false_for_any_size_other_than_324(int size)
    {
        var data = new byte[size];

        var result = TelemetryParser.TryParse(data, out var packet);

        Assert.False(result);
        Assert.Null(packet);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(323)]
    [InlineData(325)]
    [InlineData(331)]
    public void Parse_throws_ArgumentException_for_any_size_other_than_324(int size)
    {
        var data = new byte[size];

        Assert.Throws<ArgumentException>(() => TelemetryParser.Parse(data));
    }

    [Fact]
    public void PacketSize_is_324_bytes()
    {
        Assert.Equal(324, TelemetryParser.PacketSize);
    }

    // ---- Field-level decoding -------------------------------------------------
    // Each assertion below is written at its spec byte offset by PacketBuilder, independently of
    // the parser's own sequential cursor (see PacketBuilder's remarks) — so these tests actually
    // exercise the byte layout, not just "whatever the parser happens to produce".

    [Fact]
    public void Parses_IsRaceOn_true()
    {
        var data = new PacketBuilder().WithIsRaceOn(true).Build();

        var packet = TelemetryParser.Parse(data);

        Assert.True(packet.IsRaceOn);
    }

    [Fact]
    public void Parses_IsRaceOn_false()
    {
        var data = new PacketBuilder().WithIsRaceOn(false).Build();

        var packet = TelemetryParser.Parse(data);

        Assert.False(packet.IsRaceOn);
    }

    [Fact]
    public void Parses_engine_rpm_fields()
    {
        var data = new PacketBuilder()
            .WithEngineMaxRpm(7800f)
            .WithCurrentEngineRpm(4200.5f)
            .Build();

        var packet = TelemetryParser.Parse(data);

        Assert.Equal(7800f, packet.EngineMaxRpm);
        Assert.Equal(4200.5f, packet.CurrentEngineRpm);
    }

    [Fact]
    public void Parses_CarOrdinal_and_NumCylinders()
    {
        // NumCylinders is the last field before the FH6-only block; getting it right is the
        // canary for "everything up to here is correctly offset".
        var data = new PacketBuilder()
            .WithCarOrdinal(1234)
            .WithNumCylinders(8)
            .Build();

        var packet = TelemetryParser.Parse(data);

        Assert.Equal(1234, packet.CarOrdinal);
        Assert.Equal(8, packet.NumCylinders);
    }

    [Fact]
    public void Parses_the_FH6_only_block_between_NumCylinders_and_PositionX()
    {
        var data = new PacketBuilder()
            .WithCarGroup(42u)
            .WithSmashableVelDiff(12.5f)
            .WithSmashableMass(980.25f)
            .WithPositionX(100.0f)
            .Build();

        var packet = TelemetryParser.Parse(data);

        Assert.Equal(42u, packet.CarGroup);
        Assert.Equal(12.5f, packet.SmashableVelDiff);
        Assert.Equal(980.25f, packet.SmashableMass);
        // If CarGroup/SmashableVelDiff/SmashableMass were mis-offset, PositionX would read one of
        // their bytes instead of its own — this pins the field immediately after the FH6 block.
        Assert.Equal(100.0f, packet.PositionX);
    }

    [Fact]
    public void Parses_Speed_Power_and_Torque()
    {
        var data = new PacketBuilder()
            .WithSpeed(45.0f)
            .WithPower(250_000f)
            .WithTorque(450.75f)
            .Build();

        var packet = TelemetryParser.Parse(data);

        Assert.Equal(45.0f, packet.Speed);
        Assert.Equal(250_000f, packet.Power);
        Assert.Equal(450.75f, packet.Torque);
    }

    [Fact]
    public void SpeedKmh_converts_meters_per_second_to_kilometers_per_hour()
    {
        var data = new PacketBuilder().WithSpeed(10.0f).Build();

        var packet = TelemetryParser.Parse(data);

        Assert.Equal(36.0f, packet.SpeedKmh, precision: 3);
    }

    [Fact]
    public void Parses_Gear_and_Accel_inputs()
    {
        var data = new PacketBuilder()
            .WithGear(3)
            .WithAccel(255)
            .Build();

        var packet = TelemetryParser.Parse(data);

        Assert.Equal((byte)3, packet.Gear);
        Assert.Equal((byte)255, packet.Accel);
    }

    [Fact]
    public void Parses_Steer_as_a_negative_signed_byte()
    {
        // Full left steering input; must round-trip as a negative sbyte, not wrap to a large
        // positive byte value — this is exactly the bug an unsigned read would introduce.
        var data = new PacketBuilder().WithSteer(-127).Build();

        var packet = TelemetryParser.Parse(data);

        Assert.Equal((sbyte)-127, packet.Steer);
    }

    [Fact]
    public void Parses_the_last_field_in_the_packet()
    {
        // NormalizedAIBrakeDifference is the final named field (offset 322, one byte before the
        // single trailing alignment-padding byte at offset 323). Reading it correctly proves the
        // cursor walked the entire 324-byte layout without drifting.
        var data = new PacketBuilder().WithNormalizedAIBrakeDifference(-42).Build();

        var packet = TelemetryParser.Parse(data);

        Assert.Equal((sbyte)-42, packet.NormalizedAIBrakeDifference);
    }
}
