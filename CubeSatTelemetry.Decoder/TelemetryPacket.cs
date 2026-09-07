namespace CubeSatTelemetry.Decoder;

/// <summary>Current operating mode of the satellite (TM_005, 1 byte on the wire).</summary>
public enum SatelliteMode : byte
{
    Boot = 0,
    Safe = 1,
    Nominal = 2
}

/// <summary>On/off state of a subsystem (TM_006, TM_007, 1 byte on the wire).</summary>
public enum SubsystemStatus : byte
{
    Off = 0,
    On = 1
}

/// <summary>
/// One telemetry frame, and the byte map it is laid out with.
///
/// Source: OTUSAT_Telemetri_ve_Entegrasyon_Raporu.pdf, protocol v1.0.0, section 2.
///
/// Wire contract:
///   little-endian, IEEE 754 binary32 floats, 1-byte alignment (no padding),
///   39 bytes total, CRC-16/CCITT over bytes [0..36] (sync word in, CRC field out).
///
/// The offsets are written out by hand on purpose: the byte map is a contract owned by
/// the comms subsystem, not something this program is free to change, so the numbers
/// that must match the STM32 side are visible rather than derived.
/// </summary>
public sealed class TelemetryPacket
{
    public const string ProtocolVersion = "1.0.0";
    public const ushort ExpectedSyncWord = 0xABCD;
    public const int FrameSize = 39;

    // Byte offsets from the start of the frame.
    public const int OffSyncWord = 0;   // uint16
    public const int OffTimestamp = 2;   // uint32
    public const int OffTemperature = 6;   // float32
    public const int OffPressure = 10;  // float32
    public const int OffBatteryVoltage = 14;  // float32
    public const int OffBatteryCurrent = 18;  // float32
    public const int OffAttitudeRoll = 22;  // float32
    public const int OffAttitudePitch = 26;  // float32
    public const int OffAttitudeYaw = 30;  // float32
    public const int OffMode = 34;  // uint8
    public const int OffAdcsStatus = 35;  // uint8
    public const int OffCameraStatus = 36;  // uint8
    public const int OffChecksum = 37;  // uint16

    /// <summary>Bytes the CRC is computed over: everything before the CRC field.</summary>
    public const int CrcCoverage = OffChecksum;

    /// <summary>Constant frame marker; 0xABCD in a valid frame.</summary>
    public ushort SyncWord { get; set; }

    /// <summary>Uptime in milliseconds since satellite boot.</summary>
    public uint Timestamp { get; set; }

    /// <summary>TM_001, degrees Celsius.</summary>
    public float Temperature { get; set; }

    /// <summary>TM_002, hPa.</summary>
    public float Pressure { get; set; }

    /// <summary>TM_003, volts.</summary>
    public float BatteryVoltage { get; set; }

    /// <summary>TM_004, amperes.</summary>
    public float BatteryCurrent { get; set; }

    /// <summary>Rotation about the X axis, degrees. Nominal range -180 .. +180.</summary>
    public float AttitudeRoll { get; set; }

    /// <summary>Rotation about the Y axis, degrees. Nominal range -90 .. +90.</summary>
    public float AttitudePitch { get; set; }

    /// <summary>Rotation about the Z axis, degrees. Nominal range 0 .. 360.</summary>
    public float AttitudeYaw { get; set; }

    /// <summary>TM_005.</summary>
    public SatelliteMode Mode { get; set; }

    /// <summary>TM_006, attitude determination and control subsystem.</summary>
    public SubsystemStatus AdcsStatus { get; set; }

    /// <summary>TM_007, camera payload.</summary>
    public SubsystemStatus CameraStatus { get; set; }

    /// <summary>CRC-16/CCITT carried by the frame.</summary>
    public ushort Checksum { get; set; }

    // Invariant culture so the values match the STM32 side character for character
    // (24.5, never a locale's 24,5) when the two implementations are compared.
    public override string ToString() =>
        FormattableString.Invariant($"""
         SyncWord       : 0x{SyncWord:X4}
         Timestamp      : {Timestamp} ms
         Temperature    : {Temperature} °C
         Pressure       : {Pressure} hPa
         BatteryVoltage : {BatteryVoltage} V
         BatteryCurrent : {BatteryCurrent} A
         AttitudeRoll   : {AttitudeRoll} °
         AttitudePitch  : {AttitudePitch} °
         AttitudeYaw    : {AttitudeYaw} °
         Mode           : {Mode} ({(byte)Mode})
         AdcsStatus     : {AdcsStatus} ({(byte)AdcsStatus})
         CameraStatus   : {CameraStatus} ({(byte)CameraStatus})
         Checksum       : 0x{Checksum:X4}
         """);
}
