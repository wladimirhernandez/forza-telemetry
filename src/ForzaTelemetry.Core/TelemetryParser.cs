using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace ForzaTelemetry.Core;

/// <summary>
/// Decodes raw bytes received over UDP into a <see cref="TelemetryPacket"/>.
/// </summary>
public static class TelemetryParser
{
    /// <summary>
    /// Fixed size of one FH6 "Data Out" packet, in bytes, per the official spec.
    /// The last byte (offset 323) is not a named field: the 88 documented fields only add up to
    /// 323 bytes, so this trailing byte is padding the game adds to round the struct up to a
    /// multiple of 4 (the widest field type in the packet is 4 bytes). We skip it explicitly in
    /// <see cref="ParseCore"/> rather than pretending it does not exist.
    /// </summary>
    public const int PacketSize = 324;

    /// <summary>
    /// Attempts to parse a packet. Returns false (without throwing) when <paramref name="data"/>
    /// is not exactly <see cref="PacketSize"/> bytes — the shape a UDP listener loop wants, since a
    /// stray or truncated datagram should be skipped, not crash the receive loop.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, [NotNullWhen(true)] out TelemetryPacket? packet)
    {
        if (data.Length != PacketSize)
        {
            packet = null;
            return false;
        }

        packet = ParseCore(data);
        return true;
    }

    /// <summary>
    /// Parses a packet, throwing <see cref="ArgumentException"/> if <paramref name="data"/> is not
    /// exactly <see cref="PacketSize"/> bytes. Prefer <see cref="TryParse"/> when a wrong-size
    /// buffer is an expected, recoverable condition (e.g. reading a live socket).
    /// </summary>
    public static TelemetryPacket Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length != PacketSize)
        {
            throw new ArgumentException(
                $"Expected a {PacketSize}-byte Forza Horizon 6 telemetry packet, but got {data.Length} bytes.",
                nameof(data));
        }

        return ParseCore(data);
    }

    private static TelemetryPacket ParseCore(ReadOnlySpan<byte> data)
    {
        var reader = new PacketReader(data);

        var packet = new TelemetryPacket
        {
            // Race status
            IsRaceOn = reader.ReadInt32() != 0,
            TimestampMS = reader.ReadUInt32(),

            // Engine
            EngineMaxRpm = reader.ReadSingle(),
            EngineIdleRpm = reader.ReadSingle(),
            CurrentEngineRpm = reader.ReadSingle(),

            // Motion
            AccelerationX = reader.ReadSingle(),
            AccelerationY = reader.ReadSingle(),
            AccelerationZ = reader.ReadSingle(),
            VelocityX = reader.ReadSingle(),
            VelocityY = reader.ReadSingle(),
            VelocityZ = reader.ReadSingle(),
            AngularVelocityX = reader.ReadSingle(),
            AngularVelocityY = reader.ReadSingle(),
            AngularVelocityZ = reader.ReadSingle(),
            Yaw = reader.ReadSingle(),
            Pitch = reader.ReadSingle(),
            Roll = reader.ReadSingle(),

            // Wheels / tires (suspension, slip, rumble)
            NormalizedSuspensionTravelFrontLeft = reader.ReadSingle(),
            NormalizedSuspensionTravelFrontRight = reader.ReadSingle(),
            NormalizedSuspensionTravelRearLeft = reader.ReadSingle(),
            NormalizedSuspensionTravelRearRight = reader.ReadSingle(),
            TireSlipRatioFrontLeft = reader.ReadSingle(),
            TireSlipRatioFrontRight = reader.ReadSingle(),
            TireSlipRatioRearLeft = reader.ReadSingle(),
            TireSlipRatioRearRight = reader.ReadSingle(),
            WheelRotationSpeedFrontLeft = reader.ReadSingle(),
            WheelRotationSpeedFrontRight = reader.ReadSingle(),
            WheelRotationSpeedRearLeft = reader.ReadSingle(),
            WheelRotationSpeedRearRight = reader.ReadSingle(),
            WheelOnRumbleStripFrontLeft = reader.ReadInt32(),
            WheelOnRumbleStripFrontRight = reader.ReadInt32(),
            WheelOnRumbleStripRearLeft = reader.ReadInt32(),
            WheelOnRumbleStripRearRight = reader.ReadInt32(),
            WheelInPuddleFrontLeft = reader.ReadInt32(),
            WheelInPuddleFrontRight = reader.ReadInt32(),
            WheelInPuddleRearLeft = reader.ReadInt32(),
            WheelInPuddleRearRight = reader.ReadInt32(),
            SurfaceRumbleFrontLeft = reader.ReadSingle(),
            SurfaceRumbleFrontRight = reader.ReadSingle(),
            SurfaceRumbleRearLeft = reader.ReadSingle(),
            SurfaceRumbleRearRight = reader.ReadSingle(),
            TireSlipAngleFrontLeft = reader.ReadSingle(),
            TireSlipAngleFrontRight = reader.ReadSingle(),
            TireSlipAngleRearLeft = reader.ReadSingle(),
            TireSlipAngleRearRight = reader.ReadSingle(),
            TireCombinedSlipFrontLeft = reader.ReadSingle(),
            TireCombinedSlipFrontRight = reader.ReadSingle(),
            TireCombinedSlipRearLeft = reader.ReadSingle(),
            TireCombinedSlipRearRight = reader.ReadSingle(),
            SuspensionTravelMetersFrontLeft = reader.ReadSingle(),
            SuspensionTravelMetersFrontRight = reader.ReadSingle(),
            SuspensionTravelMetersRearLeft = reader.ReadSingle(),
            SuspensionTravelMetersRearRight = reader.ReadSingle(),

            // Car info
            CarOrdinal = reader.ReadInt32(),
            CarClass = reader.ReadInt32(),
            CarPerformanceIndex = reader.ReadInt32(),
            DrivetrainType = reader.ReadInt32(),
            NumCylinders = reader.ReadInt32(),

            // FH6-only block: inserted here, between NumCylinders and PositionX, compared to
            // Forza Motorsport. Get this wrong and every field below silently reads garbage.
            CarGroup = reader.ReadUInt32(),
            SmashableVelDiff = reader.ReadSingle(),
            SmashableMass = reader.ReadSingle(),

            // Position / dynamics summary
            PositionX = reader.ReadSingle(),
            PositionY = reader.ReadSingle(),
            PositionZ = reader.ReadSingle(),
            Speed = reader.ReadSingle(),
            Power = reader.ReadSingle(),
            Torque = reader.ReadSingle(),

            // Wheels / tires (temperature)
            TireTempFrontLeft = reader.ReadSingle(),
            TireTempFrontRight = reader.ReadSingle(),
            TireTempRearLeft = reader.ReadSingle(),
            TireTempRearRight = reader.ReadSingle(),

            // Engine (forced induction / fuel)
            Boost = reader.ReadSingle(),
            Fuel = reader.ReadSingle(),

            // Race
            DistanceTraveled = reader.ReadSingle(),
            BestLap = reader.ReadSingle(),
            LastLap = reader.ReadSingle(),
            CurrentLap = reader.ReadSingle(),
            CurrentRaceTime = reader.ReadSingle(),
            LapNumber = reader.ReadUInt16(),
            RacePosition = reader.ReadByte(),

            // Inputs
            Accel = reader.ReadByte(),
            Brake = reader.ReadByte(),
            Clutch = reader.ReadByte(),
            HandBrake = reader.ReadByte(),
            Gear = reader.ReadByte(),
            Steer = reader.ReadSByte(),
            NormalizedDrivingLine = reader.ReadSByte(),
            NormalizedAIBrakeDifference = reader.ReadSByte(),
        };

        // Trailing alignment padding — see the PacketSize doc comment.
        reader.Skip(1);

        Debug.Assert(
            reader.Position == PacketSize,
            $"PacketReader ended at byte {reader.Position}, expected {PacketSize}. " +
            "The field list above no longer matches the spec's byte layout.");

        return packet;
    }
}

/// <summary>
/// Reads primitive values sequentially, left to right, from a fixed little-endian byte buffer.
///
/// Why little-endian: Forza runs on Windows and Xbox, both x86/x64 (and Xbox's custom silicon,
/// which is also little-endian), so the game writes its telemetry struct to the wire exactly as
/// it sits in memory on those platforms — least-significant byte first. <see cref="BinaryPrimitives"/>
/// makes that explicit at every read instead of relying on BitConverter's platform-dependent endianness.
///
/// Why a ref struct: this type only ever wraps a <see cref="ReadOnlySpan{T}"/> that lives on the
/// stack for the duration of one <see cref="TelemetryParser.Parse(ReadOnlySpan{byte})"/> call. Making
/// it a ref struct means the compiler enforces that — it can never be boxed, stored in a field on
/// the heap, or captured by a lambda — so there is no risk of it outliving the buffer it points into.
/// A sequential cursor like this also reads far more clearly than a wall of named offset constants:
/// each call advances the position by exactly the size of the type it just read.
/// </summary>
internal ref struct PacketReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    /// <summary>How many bytes have been consumed so far. Used by the final layout assertion.</summary>
    public readonly int Position => _position;

    public int ReadInt32()
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(_position, sizeof(int)));
        _position += sizeof(int);
        return value;
    }

    public uint ReadUInt32()
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_position, sizeof(uint)));
        _position += sizeof(uint);
        return value;
    }

    public float ReadSingle()
    {
        var value = BinaryPrimitives.ReadSingleLittleEndian(_data.Slice(_position, sizeof(float)));
        _position += sizeof(float);
        return value;
    }

    public ushort ReadUInt16()
    {
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_position, sizeof(ushort)));
        _position += sizeof(ushort);
        return value;
    }

    public byte ReadByte()
    {
        var value = _data[_position];
        _position += sizeof(byte);
        return value;
    }

    public sbyte ReadSByte()
    {
        // A plain cast (not a checked conversion) is intentional: the raw byte 0xFF must become
        // -1, which is exactly what an unchecked reinterpretation of the bit pattern gives us.
        var value = unchecked((sbyte)_data[_position]);
        _position += sizeof(sbyte);
        return value;
    }

    /// <summary>Advances the cursor past bytes that carry no field — e.g. trailing struct padding.</summary>
    public void Skip(int byteCount) => _position += byteCount;
}
