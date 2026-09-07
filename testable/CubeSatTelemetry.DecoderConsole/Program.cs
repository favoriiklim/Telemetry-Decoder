using System.Text;
using CubeSatTelemetry.Decoder;

// Test console for the decoder library. Input is a hex block, a binary block, a .bin
// path, or a single line of any of those as a command-line argument. Input longer than
// one frame is treated as a stream and every frame in it is reported.

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine($"OTUSAT Telemetry DECODER  |  protocol v{TelemetryPacket.ProtocolVersion}, " +
                  $"{TelemetryPacket.FrameSize} bytes, little-endian, " +
                  $"sync 0x{TelemetryPacket.ExpectedSyncWord:X4}, " +
                  $"CRC over first {TelemetryPacket.CrcCoverage} bytes");

if (!Crc16Ccitt.SelfTest())
{
    Console.WriteLine("CRC self-test FAILED; decoding cannot be trusted.");
    return 1;
}

if (args.Length > 0)
{
    return Run(string.Join(string.Empty, args), interactive: false) ? 0 : 1;
}

Console.WriteLine("Paste a hex block, a binary block, or a .bin file path, then a blank");
Console.WriteLine("line to decode it. A blank line on its own quits.");

// Input is collected across lines: a pasted dump arrives as several lines, and reading
// only the first one would decode 16 bytes and report TooShort.
var buffer = new StringBuilder();

while (true)
{
    Console.Write(buffer.Length == 0 ? "\n> " : "  ");
    var line = Console.ReadLine();

    if (line is null)
    {
        // End of piped input: decode whatever was collected, then stop.
        return buffer.Length > 0 && !Run(buffer.ToString(), interactive: false) ? 1 : 0;
    }

    if (line.Trim().Length == 0)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        Run(buffer.ToString(), interactive: true);
        buffer.Clear();
        continue;
    }

    buffer.AppendLine(line);
}

// Reads the input, decodes it, prints the report, then offers to save.
static bool Run(string input, bool interactive)
{
    byte[] bytes;
    try
    {
        bytes = ReadInput(input.Trim());
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Could not read input: {ex.Message}");
        return false;
    }

    // One frame's worth or less is decoded directly, so the reason a bad frame failed
    // still comes through. Anything longer is a stream and may hold several frames.
    var (text, success) = bytes.Length > TelemetryPacket.FrameSize
        ? BuildStreamReport(bytes)
        : BuildSingleReport(bytes);

    Console.Write(text);

    if (interactive)
    {
        OfferSave(bytes, text);
    }

    return success;
}

// Every frame found in a stream, each with its own dump.
static (string Text, bool Success) BuildStreamReport(byte[] stream)
{
    var frames = TelemetryDecoder.DecodeAll(stream);

    var sb = new StringBuilder();
    sb.AppendLine();
    sb.AppendLine($"Input      : {stream.Length} bytes");

    if (frames.Count == 0)
    {
        sb.AppendLine("Framing    : no valid frame found (no sync word with a matching CRC).");
        return (sb.ToString(), false);
    }

    sb.AppendLine($"Framing    : {frames.Count} frame(s) at offset " +
                  $"{string.Join(", ", frames.Select(f => f.Offset))}");

    for (var i = 0; i < frames.Count; i++)
    {
        sb.AppendLine();
        sb.AppendLine(new string('-', 70));
        sb.AppendLine($"FRAME {i + 1} of {frames.Count}   offset {frames[i].Offset}");
        sb.AppendLine(new string('-', 70));
        AppendDumps(sb, stream.AsSpan(frames[i].Offset, TelemetryPacket.FrameSize));
        sb.AppendLine("DECODED");
        sb.AppendLine(frames[i].Packet.ToString());
    }

    return (sb.ToString(), true);
}

// A single frame, where the failure reason matters.
static (string Text, bool Success) BuildSingleReport(byte[] bytes)
{
    var result = TelemetryDecoder.Decode(bytes);

    var sb = new StringBuilder();
    sb.AppendLine();
    sb.AppendLine($"Input      : {bytes.Length} bytes");
    sb.AppendLine();
    AppendDumps(sb, bytes);

    if (result.IsSuccess)
    {
        sb.AppendLine("DECODED");
        sb.AppendLine(result.Packet!.ToString());
    }
    else
    {
        sb.AppendLine($"DECODE FAILED -> {result}");
    }

    return (sb.ToString(), result.IsSuccess);
}

static void AppendDumps(StringBuilder sb, ReadOnlySpan<byte> data)
{
    sb.AppendLine("HEX");
    sb.Append(FrameText.Hex(data));
    sb.AppendLine();
    sb.AppendLine("BINARY");
    sb.Append(FrameText.Bits(data));
    sb.AppendLine();
}

static void OfferSave(byte[] bytes, string report)
{
    Console.Write("Save? [b] input as .bin  [t] report as .txt  [Enter] skip: ");
    var choice = Console.ReadLine()?.Trim().ToLowerInvariant();

    if (choice is not ("b" or "t"))
    {
        return;
    }

    var isBinary = choice == "b";
    var fallback = isBinary ? "capture.bin" : "report.txt";

    Console.Write($"File name (empty = {fallback}): ");
    var name = Console.ReadLine();
    var path = Path.GetFullPath(string.IsNullOrWhiteSpace(name) ? fallback : name.Trim());

    try
    {
        if (isBinary)
        {
            File.WriteAllBytes(path, bytes);
        }
        else
        {
            File.WriteAllText(path, report, new UTF8Encoding(false));
        }

        Console.WriteLine($"Written: {path}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Could not write: {ex.Message}");
    }
}

// An existing path is read as raw bytes; anything else is treated as hex text.
static byte[] ReadInput(string input)
{
    if (File.Exists(input))
    {
        Console.WriteLine($"Reading from file: {Path.GetFullPath(input)}");
        return File.ReadAllBytes(input);
    }

    // A hex frame is only 0-9 and A-F, so a separator or an extension means the operator
    // meant a file. Without this check the path text falls through to the hex parser and
    // decodes into nonsense that looks like a decoder bug rather than a typo.
    if (input.Contains('/') || input.Contains('\\') || input.Contains(':') || Path.HasExtension(input))
    {
        throw new FileNotFoundException($"File not found: {Path.GetFullPath(input)}");
    }

    var cleaned = new string(input.Where(Uri.IsHexDigit).ToArray());

    if (cleaned.Length == 0)
    {
        throw new FormatException("No hex digits in the input, and no file by that name.");
    }

    // A pasted BINARY block is nothing but 0s and 1s - which are also valid hex digits,
    // so without this it would be parsed as hex and decode into nonsense. A real frame
    // carries the 0xABCD sync word, so it can never be all 0s and 1s.
    if (cleaned.Length % 8 == 0 && cleaned.All(c => c is '0' or '1'))
    {
        var bits = new byte[cleaned.Length / 8];
        for (var i = 0; i < bits.Length; i++)
        {
            bits[i] = Convert.ToByte(cleaned.Substring(i * 8, 8), 2);
        }

        Console.WriteLine($"Input read as binary: {bits.Length} bytes.");
        return bits;
    }

    if (cleaned.Length % 2 != 0)
    {
        throw new FormatException($"Not valid hex ({cleaned.Length} hex characters; must be even).");
    }

    return Convert.FromHexString(cleaned);
}
