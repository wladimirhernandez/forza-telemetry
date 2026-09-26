using System.Buffers.Binary;

namespace ForzaTelemetry.Core.Tests;

/// <summary>
/// Builds a 324-byte fake telemetry packet for tests, writing each value at its byte offset
/// straight from the spec — independently of <see cref="TelemetryParser"/>'s own sequential
/// cursor. That independence is the whole point: if the parser's cursor and this builder's
/// offsets ever disagree, a test built on top of it should fail, catching an offset mistake
/// that two versions of the same "read the cursor forward" logic could otherwise share.
/// </summary>
internal sealed class PacketBuilder
{
    // Offsets taken directly from the official FH6 spec (see TelemetryPacket.cs remarks for the
    // source link), not from the parser. Only the fields the tests care about are listed; the
    // remaining 324 bytes stay zero-filled, which is a valid (if uninteresting) packet.
    private const int OffsetIsRaceOn = 0;
    private const int OffsetEngineMaxRpm = 8;
    private const int OffsetCurrentEngineRpm = 16;
    private const int OffsetCarOrdinal = 212;
    private const int OffsetNumCylinders = 228;
    private const int OffsetCarGroup = 232;
    private const int OffsetSmashableVelDiff = 236;
    private const int OffsetSmashableMass = 240;
    private const int OffsetPositionX = 244;
    private const int OffsetSpeed = 256;
    private const int OffsetPower = 260;
    private const int OffsetTorque = 264;
    private const int OffsetAccel = 315;
    private const int OffsetGear = 319;
    private const int OffsetSteer = 320;
    private const int OffsetNormalizedAIBrakeDifference = 322;

    private readonly byte[] _buffer = new byte[TelemetryParser.PacketSize];

    public byte[] Build() => _buffer;

    public PacketBuilder WithIsRaceOn(bool value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(Span(OffsetIsRaceOn), value ? 1 : 0);
        return this;
    }

    public PacketBuilder WithEngineMaxRpm(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(Span(OffsetEngineMaxRpm), value);
        return this;
    }

    public PacketBuilder WithCurrentEngineRpm(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(Span(OffsetCurrentEngineRpm), value);
        return this;
    }

    public PacketBuilder WithCarOrdinal(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(Span(OffsetCarOrdinal), value);
        return this;
    }

    public PacketBuilder WithNumCylinders(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(Span(OffsetNumCylinders), value);
        return this;
    }

    public PacketBuilder WithCarGroup(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(Span(OffsetCarGroup), value);
        return this;
    }

    public PacketBuilder WithSmashableVelDiff(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(Span(OffsetSmashableVelDiff), value);
        return this;
    }

    public PacketBuilder WithSmashableMass(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(Span(OffsetSmashableMass), value);
        return this;
    }

    public PacketBuilder WithPositionX(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(Span(OffsetPositionX), value);
        return this;
    }

    public PacketBuilder WithSpeed(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(Span(OffsetSpeed), value);
        return this;
    }

    public PacketBuilder WithPower(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(Span(OffsetPower), value);
        return this;
    }

    public PacketBuilder WithTorque(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(Span(OffsetTorque), value);
        return this;
    }

    public PacketBuilder WithAccel(byte value)
    {
        _buffer[OffsetAccel] = value;
        return this;
    }

    public PacketBuilder WithGear(byte value)
    {
        _buffer[OffsetGear] = value;
        return this;
    }

    public PacketBuilder WithSteer(sbyte value)
    {
        _buffer[OffsetSteer] = unchecked((byte)value);
        return this;
    }

    public PacketBuilder WithNormalizedAIBrakeDifference(sbyte value)
    {
        _buffer[OffsetNormalizedAIBrakeDifference] = unchecked((byte)value);
        return this;
    }

    private Span<byte> Span(int offset) => _buffer.AsSpan(offset, sizeof(int));
}
