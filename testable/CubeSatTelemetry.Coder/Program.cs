using System.Globalization;
using System.Text;
using CubeSatTelemetry.Coder;
using CubeSatTelemetry.Decoder;

// Coder console: asks for the field values, produces the wire bytes, prints the whole
// result. This is a test fixture. The decoder does not reference it.

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine($"OTUSAT Telemetry CODER (test data generator)  |  protocol v{TelemetryPacket.ProtocolVersion}, " +
                  $"{TelemetryPacket.FrameSize} bytes, little-endian, " +
                  $"sync 0x{TelemetryPacket.ExpectedSyncWord:X4}");
Console.WriteLine($"CRC self-test: {(Crc16Ccitt.SelfTest() ? "OK" : "FAILED")}");

while (true)
{
    var packet = AskPacket();
    var frame = TelemetryEncoder.Encode(packet);

    Console.Write(BuildReport(packet, frame));

    Console.Write("\n[Enter] new packet   [q] quit: ");
    var again = Console.ReadLine();
    if (again is null || again.Trim().StartsWith('q'))
    {
        return 0;
    }
}

// Asks for every field the operator controls. SyncWord and Checksum are not asked for:
// they are derived by the encoder.
static TelemetryPacket AskPacket()
{
    Console.WriteLine();
    Console.WriteLine(new string('-', 70));
    Console.WriteLine("Enter the field values. Empty input keeps the value in brackets.");
    Console.WriteLine("Both ',' and '.' are accepted as the decimal separator.");
    Console.WriteLine(new string('-', 70));

    return new TelemetryPacket
    {
        Timestamp = AskUInt32("Timestamp", "ms", 123_456_789),
        Temperature = AskFloat("Temperature", "°C", 24.5f),
        Pressure = AskFloat("Pressure", "hPa", 1013.25f),
        BatteryVoltage = AskFloat("BatteryVoltage", "V", 7.4f),
        BatteryCurrent = AskFloat("BatteryCurrent", "A", 0.35f),
        AttitudeRoll = AskFloat("AttitudeRoll", "°", -12.75f),
        AttitudePitch = AskFloat("AttitudePitch", "°", 3.5f),
        AttitudeYaw = AskFloat("AttitudeYaw", "°", 180.0f),
        Mode = AskEnum("Mode", SatelliteMode.Nominal),
        AdcsStatus = AskEnum("AdcsStatus", SubsystemStatus.On),
        CameraStatus = AskEnum("CameraStatus", SubsystemStatus.Off)
    };
}

static string BuildReport(TelemetryPacket packet, byte[] frame)
{
    var check = TelemetryDecoder.Decode(frame);

    var sb = new StringBuilder();
    sb.AppendLine();
    sb.AppendLine(new string('=', 70));
    sb.AppendLine($"GENERATED FRAME  ({frame.Length} bytes, CRC 0x{packet.Checksum:X4} " +
                  $"over the first {TelemetryPacket.CrcCoverage} bytes)");
    sb.AppendLine(new string('=', 70));
    sb.AppendLine();
    sb.AppendLine("Any of the three blocks below can be pasted into the decoder as-is.");
    sb.AppendLine();
    sb.AppendLine("HEX");
    sb.Append(FrameText.Hex(frame));
    sb.AppendLine();
    sb.AppendLine("BINARY");
    sb.Append(FrameText.Bits(frame));
    sb.AppendLine();
    sb.AppendLine("HEX, SINGLE LINE");
    sb.AppendLine(FrameText.HexLine(frame));
    sb.AppendLine();

    // Sanity check: whatever the coder produced must survive the decoder unchanged.
    sb.AppendLine($"ROUND-TRIP CHECK: {(check.IsSuccess ? "OK" : $"FAILED -> {check}")}");
    if (check.IsSuccess)
    {
        sb.AppendLine();
        sb.AppendLine("DECODED");
        sb.AppendLine(check.Packet!.ToString());
    }

    return sb.ToString();
}

static uint AskUInt32(string name, string unit, uint fallback)
{
    while (true)
    {
        var raw = Prompt(name, unit, fallback.ToString());
        if (raw.Length == 0)
        {
            return fallback;
        }

        if (uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        Console.WriteLine($"  '{raw}' is not a valid integer (0 .. {uint.MaxValue}).");
    }
}

static float AskFloat(string name, string unit, float fallback)
{
    while (true)
    {
        var raw = Prompt(name, unit, fallback.ToString(CultureInfo.InvariantCulture));
        if (raw.Length == 0)
        {
            return fallback;
        }

        // Accept a decimal comma as well as the invariant dot.
        if (float.TryParse(raw.Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        Console.WriteLine($"  '{raw}' is not a valid decimal number.");
    }
}

static TEnum AskEnum<TEnum>(string name, TEnum fallback) where TEnum : struct, Enum
{
    var options = string.Join(", ", Enum.GetValues<TEnum>()
        .Select(v => $"{Convert.ToByte(v)}={v}"));

    while (true)
    {
        var raw = Prompt($"{name} [{options}]", string.Empty, fallback.ToString());
        if (raw.Length == 0)
        {
            return fallback;
        }

        // Names and raw numbers are both accepted; an out-of-ICD number is allowed
        // through on purpose, so the decoder's handling of it can be tested.
        if (Enum.TryParse<TEnum>(raw, ignoreCase: true, out var value))
        {
            return value;
        }

        Console.WriteLine($"  '{raw}' is invalid. Expected: {options}");
    }
}

static string Prompt(string name, string unit, string fallback)
{
    var suffix = string.IsNullOrEmpty(unit) ? string.Empty : $" [{unit}]";
    Console.Write($"  {name}{suffix} ({fallback}): ");
    return Console.ReadLine()?.Trim() ?? string.Empty;
}
