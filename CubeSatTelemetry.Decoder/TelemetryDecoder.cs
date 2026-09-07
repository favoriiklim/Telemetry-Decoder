using System.Buffers.Binary;
using System.Text;

namespace CubeSatTelemetry.Decoder;

/// <summary>
/// Renders raw frame bytes as text. These are what you put next to the STM32 output
/// when the two implementations disagree about a byte.
///
/// No offset column: an offset label such as "0010" is itself made of hex digits, so a
/// dump carrying one cannot be pasted back into the decoder - the offsets would be read
/// as data. Every block these methods produce is copy-paste safe.
/// </summary>
public static class FrameText
{
    /// <summary>Hex dump, 16 bytes per line.</summary>
    public static string Hex(ReadOnlySpan<byte> data) => Dump(data, 16, b => $"{b:X2}");

    /// <summary>Bit dump, 8 bytes per line, most significant bit first.</summary>
    public static string Bits(ReadOnlySpan<byte> data)
        => Dump(data, 8, b => Convert.ToString(b, 2).PadLeft(8, '0'));

    /// <summary>Single line of hex, the compact form for pasting into a ticket or chat.</summary>
    public static string HexLine(ReadOnlySpan<byte> data) => Convert.ToHexString(data);

    // Separator goes before each byte rather than after, so no line ends in whitespace.
    private static string Dump(ReadOnlySpan<byte> data, int perLine, Func<byte, string> format)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < data.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(i % perLine == 0 ? Environment.NewLine : " ");
            }

            sb.Append(format(data[i]));
        }

        return sb.AppendLine().ToString();
    }
}

/// <summary>One frame found in a stream, with the byte offset it was found at.</summary>
public sealed record DecodedFrame(int Offset, TelemetryPacket Packet);

/// <summary>Reasons a frame can fail to decode.</summary>
public enum FrameError
{
    None = 0,
    TooShort,
    BadSyncWord,
    ChecksumMismatch
}

/// <summary>
/// Outcome of a decode attempt. A corrupt frame is normal on a radio link, so failure is
/// an ordinary return value here rather than an exception: the caller drops the frame and
/// carries on with the next one.
/// </summary>
public sealed record DecodeResult(TelemetryPacket? Packet, FrameError Error, string Message)
{
    public bool IsSuccess => Error == FrameError.None;

    public override string ToString() => IsSuccess ? "OK" : $"{Error}: {Message}";
}

/// <summary>CRC-16/CCITT: poly=0x1021, init=0xFFFF, no reflection, no final xor.</summary>
public static class Crc16Ccitt
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b << 8;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
            }
        }

        return (ushort)crc;
    }

    /// <summary>
    /// The standard check vector. Run the same test on the STM32 side before comparing
    /// any frame between the two implementations.
    /// </summary>
    public static bool SelfTest() => Compute("123456789"u8) == 0x29B1;
}

/// <summary>
/// Turns 39 raw bytes into a <see cref="TelemetryPacket"/>.
///
/// Every read states its byte order explicitly through BinaryPrimitives.*LittleEndian.
/// The protocol is little-endian because that is native on both the STM32 and on x86,
/// but writing it out explicitly keeps the result correct even if the ground segment is
/// ever run on a big-endian machine. Nothing is memcpy'd out of a struct: that would
/// silently take the host byte order and add whatever padding the compiler chose.
/// </summary>
public static class TelemetryDecoder
{
    /// <summary>
    /// The integration entry point named in the protocol report: parses a frame, or
    /// reports why it could not be parsed.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> source, out TelemetryPacket? packet, out string error)
    {
        var result = Decode(source);
        packet = result.Packet;
        error = result.IsSuccess ? string.Empty : result.Message;
        return result.IsSuccess;
    }

    /// <summary>
    /// Checks length, sync word and CRC, then reads the fields.
    /// Never throws on bad input; the reason comes back in the result.
    /// </summary>
    public static DecodeResult Decode(ReadOnlySpan<byte> source)
    {
        if (source.Length < TelemetryPacket.FrameSize)
        {
            return new DecodeResult(null, FrameError.TooShort,
                $"Frame must be {TelemetryPacket.FrameSize} bytes, got {source.Length}.");
        }

        var frame = source[..TelemetryPacket.FrameSize];

        var sync = BinaryPrimitives.ReadUInt16LittleEndian(frame[TelemetryPacket.OffSyncWord..]);
        if (sync != TelemetryPacket.ExpectedSyncWord)
        {
            return new DecodeResult(null, FrameError.BadSyncWord,
                $"Expected sync word 0x{TelemetryPacket.ExpectedSyncWord:X4}, found 0x{sync:X4}.");
        }

        var expected = Crc16Ccitt.Compute(frame[..TelemetryPacket.CrcCoverage]);
        var actual = BinaryPrimitives.ReadUInt16LittleEndian(frame[TelemetryPacket.OffChecksum..]);
        if (actual != expected)
        {
            return new DecodeResult(null, FrameError.ChecksumMismatch,
                $"CRC mismatch: computed 0x{expected:X4}, frame carries 0x{actual:X4}. Frame is corrupt.");
        }

        return new DecodeResult(ReadFields(frame), FrameError.None, "OK");
    }

    /// <summary>
    /// Extracts every valid frame in a stream, in the order they appear. A LoRa read can
    /// hand over several packets at once, so returning only the first would silently drop
    /// the rest.
    ///
    /// Anything between, before or after the frames - noise, a partial frame at either
    /// end - is discarded. Nothing is buffered for a later call.
    /// </summary>
    public static List<DecodedFrame> DecodeAll(ReadOnlySpan<byte> stream)
    {
        var frames = new List<DecodedFrame>();
        var offset = 0;

        while (offset + TelemetryPacket.FrameSize <= stream.Length)
        {
            var relative = FindFrameStart(stream[offset..]);
            if (relative < 0)
            {
                break;
            }

            var start = offset + relative;
            frames.Add(new DecodedFrame(start, ReadFields(stream.Slice(start, TelemetryPacket.FrameSize))));

            // Resume past the frame just taken, so its bytes cannot match again.
            offset = start + TelemetryPacket.FrameSize;
        }

        return frames;
    }

    /// <summary>
    /// Finds the first valid frame in a raw byte stream and returns its start index, or
    /// -1. This is what turns the continuous stream off the radio into discrete frames.
    /// </summary>
    public static int FindFrameStart(ReadOnlySpan<byte> stream)
    {
        // Little-endian, so the sync word 0xABCD sits on the wire as CD AB.
        const byte first = (byte)(TelemetryPacket.ExpectedSyncWord & 0xFF);
        const byte second = (byte)(TelemetryPacket.ExpectedSyncWord >> 8);

        for (var i = 0; i + TelemetryPacket.FrameSize <= stream.Length; i++)
        {
            // The sync word only makes a candidate - those two bytes also occur inside
            // noise and inside float values. The CRC is what actually confirms it.
            if (stream[i] == first && stream[i + 1] == second &&
                Decode(stream.Slice(i, TelemetryPacket.FrameSize)).IsSuccess)
            {
                return i;
            }
        }

        return -1;
    }

    // Reads the fields with no validation; only reached once the frame has passed the
    // length, sync word and CRC checks above.
    private static TelemetryPacket ReadFields(ReadOnlySpan<byte> frame) => new()
    {
        SyncWord = BinaryPrimitives.ReadUInt16LittleEndian(frame[TelemetryPacket.OffSyncWord..]),
        Timestamp = BinaryPrimitives.ReadUInt32LittleEndian(frame[TelemetryPacket.OffTimestamp..]),
        Temperature = BinaryPrimitives.ReadSingleLittleEndian(frame[TelemetryPacket.OffTemperature..]),
        Pressure = BinaryPrimitives.ReadSingleLittleEndian(frame[TelemetryPacket.OffPressure..]),
        BatteryVoltage = BinaryPrimitives.ReadSingleLittleEndian(frame[TelemetryPacket.OffBatteryVoltage..]),
        BatteryCurrent = BinaryPrimitives.ReadSingleLittleEndian(frame[TelemetryPacket.OffBatteryCurrent..]),
        AttitudeRoll = BinaryPrimitives.ReadSingleLittleEndian(frame[TelemetryPacket.OffAttitudeRoll..]),
        AttitudePitch = BinaryPrimitives.ReadSingleLittleEndian(frame[TelemetryPacket.OffAttitudePitch..]),
        AttitudeYaw = BinaryPrimitives.ReadSingleLittleEndian(frame[TelemetryPacket.OffAttitudeYaw..]),

        // An out-of-ICD value (Mode = 7, say) is carried through as-is rather than
        // rejected; losing telemetry is worse than reporting an unknown value. Flag it
        // upstream with Enum.IsDefined if that matters.
        Mode = (SatelliteMode)frame[TelemetryPacket.OffMode],
        AdcsStatus = (SubsystemStatus)frame[TelemetryPacket.OffAdcsStatus],
        CameraStatus = (SubsystemStatus)frame[TelemetryPacket.OffCameraStatus],

        Checksum = BinaryPrimitives.ReadUInt16LittleEndian(frame[TelemetryPacket.OffChecksum..])
    };
}
