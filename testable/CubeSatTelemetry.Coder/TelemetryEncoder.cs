using System.Buffers.Binary;
using CubeSatTelemetry.Decoder;

namespace CubeSatTelemetry.Coder;

/// <summary>
/// Turns a <see cref="TelemetryPacket"/> into 39 wire bytes, so the decoder can be tested
/// without a satellite. Not part of the decoder deliverable: the byte map and the CRC are
/// taken from the decoder side, and nothing over there references this.
/// </summary>
public static class TelemetryEncoder
{
    public static byte[] Encode(TelemetryPacket packet)
    {
        var buffer = new byte[TelemetryPacket.FrameSize];
        var frame = buffer.AsSpan();

        BinaryPrimitives.WriteUInt16LittleEndian(frame[TelemetryPacket.OffSyncWord..],
            TelemetryPacket.ExpectedSyncWord);
        BinaryPrimitives.WriteUInt32LittleEndian(frame[TelemetryPacket.OffTimestamp..], packet.Timestamp);
        BinaryPrimitives.WriteSingleLittleEndian(frame[TelemetryPacket.OffTemperature..], packet.Temperature);
        BinaryPrimitives.WriteSingleLittleEndian(frame[TelemetryPacket.OffPressure..], packet.Pressure);
        BinaryPrimitives.WriteSingleLittleEndian(frame[TelemetryPacket.OffBatteryVoltage..], packet.BatteryVoltage);
        BinaryPrimitives.WriteSingleLittleEndian(frame[TelemetryPacket.OffBatteryCurrent..], packet.BatteryCurrent);
        BinaryPrimitives.WriteSingleLittleEndian(frame[TelemetryPacket.OffAttitudeRoll..], packet.AttitudeRoll);
        BinaryPrimitives.WriteSingleLittleEndian(frame[TelemetryPacket.OffAttitudePitch..], packet.AttitudePitch);
        BinaryPrimitives.WriteSingleLittleEndian(frame[TelemetryPacket.OffAttitudeYaw..], packet.AttitudeYaw);

        frame[TelemetryPacket.OffMode] = (byte)packet.Mode;
        frame[TelemetryPacket.OffAdcsStatus] = (byte)packet.AdcsStatus;
        frame[TelemetryPacket.OffCameraStatus] = (byte)packet.CameraStatus;

        // The CRC covers everything written so far, sync word included.
        var crc = Crc16Ccitt.Compute(frame[..TelemetryPacket.CrcCoverage]);
        BinaryPrimitives.WriteUInt16LittleEndian(frame[TelemetryPacket.OffChecksum..], crc);

        // SyncWord and Checksum are derived, so whatever the caller set is overwritten
        // and written back onto the object: it always matches the bytes going out.
        packet.SyncWord = TelemetryPacket.ExpectedSyncWord;
        packet.Checksum = crc;

        return buffer;
    }
}
