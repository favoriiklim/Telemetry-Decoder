# OTUSAT Telemetry Decoder

Ground segment decoder for the OTUSAT CubeSat binary telemetry frame, with a test
console and a test data generator alongside it.

**Scope note.** This project is the testable form of the core decoding algorithm — it
does the byte-level work and nothing else. It will need some small changes before it
runs inside the ground station application itself.

## Data Path

```
LoRa packet  →  Ground Station  →  Packet Decoder  →  Telemetry object
                                   (this project)
```

The decoding path is raw bytes end to end: `TryParse`, `Decode`, `DecodeAll` and
`FindFrameStart` all take a `ReadOnlySpan<byte>`. Hex and binary text exist only as
console input formats, so a frame can be pasted in by hand during testing.

## Source of Truth

`OTUSAT_Telemetri_ve_Entegrasyon_Raporu.pdf` — protocol v1.0.0, prepared by the
communication subsystem lead. Section 2 of that report defines the byte map below.

Implemented here: section 2 (the telemetry packet byte map) and the parsing side of
section 4. The telecommand (TC) list, the STM32 flight-software side and the database,
charting and 3D-attitude integration are later work, deliberately out of this project.

## Wire Contract

| Rule | Value |
|---|---|
| Byte order | **little-endian** |
| Floating point | **IEEE 754** binary32 |
| Alignment | **1-byte packing, no padding** |
| Frame size | **39 bytes** |
| Sync word | `0xABCD` (on the wire as `CD AB`) |
| CRC | **CRC-16/CCITT** — `poly=0x1021, init=0xFFFF, refin=false, refout=false, xorout=0x0000` |
| CRC coverage | Bytes `[0..36]` — sync word **included**, CRC field **excluded** |

Little-endian is the protocol's choice because it is the native order on both the STM32
Cortex-M and on the x86 ground station, so neither side pays a conversion cost.

## Byte Map

Mirrored in code by the `Off*` constants in `TelemetryPacket.cs`.

| Offset | Size | Field | Type | Unit | Notes |
|---:|---:|---|---|---|---|
| 0 | 2 | SyncWord | uint16 | - | Constant `0xABCD` |
| 2 | 4 | Timestamp | uint32 | ms | Uptime since boot |
| 6 | 4 | Temperature | float32 | °C | TM_001 |
| 10 | 4 | Pressure | float32 | hPa | TM_002 |
| 14 | 4 | BatteryVoltage | float32 | V | TM_003 |
| 18 | 4 | BatteryCurrent | float32 | A | TM_004 |
| 22 | 4 | AttitudeRoll | float32 | ° | -180 .. +180 |
| 26 | 4 | AttitudePitch | float32 | ° | -90 .. +90 |
| 30 | 4 | AttitudeYaw | float32 | ° | 0 .. 360 |
| 34 | 1 | Mode | uint8 | enum | TM_005: 0=Boot, 1=Safe, 2=Nominal |
| 35 | 1 | AdcsStatus | uint8 | enum | TM_006: 0=Off, 1=On |
| 36 | 1 | CameraStatus | uint8 | enum | TM_007: 0=Off, 1=On |
| 37 | 2 | Checksum | uint16 | - | CRC-16/CCITT over bytes 0..36 |

The byte map is an **external contract** owned by the comms subsystem. The offsets are
therefore written out by hand rather than derived, so the numbers that have to match the
STM32 side are visible in the source.

If the map changes: update the `Off*` constant, `FrameSize` and the property in
`TelemetryPacket.cs`, then add one line each to `TelemetryDecoder.ReadFields` and
`TelemetryEncoder.Encode`. Four places, all next to each other.

## Layout

```
CubeSatTelemetry.Decoder/                  class library — the deliverable
  TelemetryPacket.cs              112      packet class + enums + byte map constants
  TelemetryDecoder.cs             226      FrameText + CRC + TryParse/Decode/DecodeAll/FindFrameStart

testable/                                  test tools, not part of the deliverable
  CubeSatTelemetry.DecoderConsole/
    Program.cs                    235      console: take hex/binary/.bin, decode, report, save
  CubeSatTelemetry.Coder/
    TelemetryEncoder.cs            44      Encode
    Program.cs                    160      interactive console, prints the result directly
```

The decoder is a class library with no console, no I/O and no entry point — that is what
the ground station application references. Everything under `testable/` is a tool for
exercising it and can be deleted without touching the library; the dependency only runs
one way.

## Parsing API

The entry point named in the protocol report:

```csharp
if (TelemetryDecoder.TryParse(rawBytes, out var packet, out var error))
{
    // packet.Temperature, packet.BatteryVoltage, ...
}
else
{
    Console.WriteLine($"[CORRUPT FRAME] {error}");
}
```

| Method | Use |
|---|---|
| `TryParse(bytes, out packet, out error)` | One frame, boolean result plus a message |
| `Decode(bytes)` | One frame, typed `DecodeResult` — distinguishes `TooShort`, `BadSyncWord`, `ChecksumMismatch` |
| `DecodeAll(stream)` | **Every** frame in a longer buffer, each with the offset it was found at |
| `FindFrameStart(stream)` | Offset of the first valid frame, or -1 |

A LoRa read can hand over several packets at once, so `DecodeAll` walks the whole buffer
and returns all of them rather than stopping at the first:

```csharp
foreach (var (offset, packet) in TelemetryDecoder.DecodeAll(buffer))
{
    Console.WriteLine($"frame at {offset}: {packet.Temperature} °C");
}
```

Noise before, between and after the frames is skipped. A partial frame at either end is
discarded — nothing is carried over to a later call.

Every frame is validated three ways before its fields are read: length, sync word, then
CRC over the first 37 bytes. A frame that fails any of them is never returned as data.

## Running

### Decoder console

```bash
dotnet run --project testable/CubeSatTelemetry.DecoderConsole
```

Accepts:

- **hex** — a single line or a multi-line block
- **binary** — a block of 0s and 1s (the coder's `BINARY` output pastes straight in)
- **a .bin file path** — raw bytes, exactly as they come off the radio

Paste, then press **Enter on a blank line** to decode. A blank line on its own quits.
Input is collected across lines because a pasted dump arrives as several of them.

Input longer than one frame is treated as a stream, and every frame in it is reported:

```
Input      : 124 bytes
Framing    : 3 frame(s) at offset 3, 44, 84

----------------------------------------------------------------------
FRAME 1 of 3   offset 3
----------------------------------------------------------------------
HEX
...
DECODED
Timestamp      : 1000 ms
...
```

A single line can also be passed as an argument:

```bash
CubeSatTelemetry.DecoderConsole.exe CDAB15CD5B07...
CubeSatTelemetry.DecoderConsole.exe "C:\Temp\capture.bin"
```

The full report always goes to the console — `HEX`, `BINARY` and `DECODED` — followed by
an optional save:

```
Save? [b] input as .bin  [t] report as .txt  [Enter] skip:
```

> `dotnet run -- <argument>` mangles path characters in some environments. To pass an
> argument, call the built executable directly, or paste the path in interactive mode.

### Coder

```bash
dotnet run --project testable/CubeSatTelemetry.Coder
```

Prompts for each field (empty input keeps the default; `,` and `.` both work as the
decimal separator), builds the frame and prints the result **straight to the console**:
`HEX`, `BINARY`, `HEX, SINGLE LINE`, the round-trip check and the `DECODED` fields. All
three blocks paste into the decoder as-is. Then one question:

```
[Enter] new packet   [q] quit:
```

To exercise the decoder's error paths, copy the `HEX, SINGLE LINE` output and edit it:

| Edit | Expected result |
|---|---|
| Change one hex character in the middle | `ChecksumMismatch` |
| Change the leading `CDAB` to `DEAD` | `BadSyncWord` |
| Trim characters off the end | `TooShort` |
| Paste two frames back to back | Both reported, with their offsets |

## Design Decisions

**Why not `StructLayout` / `Marshal`?** `Marshal.StructureToPtr` uses the **host**
machine's byte order and whatever padding the compiler chose. Reading each field through
`BinaryPrimitives.*LittleEndian` states the byte order explicitly on every line, so the
result stays correct regardless of the machine the ground segment runs on.

**Why no exceptions?** A corrupt frame is normal on a radio link, not an exceptional
condition. `Decode` never throws; it returns the reason (`TooShort` / `BadSyncWord` /
`ChecksumMismatch`) so the caller can drop the frame and move on to the next one.

**Why isn't the sync word enough?** The byte pair `CD AB` also occurs inside noise and
inside float values. `FindFrameStart` treats the sync word as a **candidate** only; the
CRC is what confirms a frame.

**Why invariant culture?** The output gets compared line by line against the STM32 side,
which prints `24.5` — a locale that prints `24,5` would break that comparison.

**Why no offset column in the dumps?** An offset label like `0010` is itself made of hex
digits. With that column present, copying the `HEX` block into the decoder would feed the
offsets in as data and silently decode the wrong frame. Every block `FrameText` produces
is safe to paste.

## Known Behavior

An out-of-spec enum value (`Mode = 7`, say) is carried through as-is rather than
rejected. That is the right behaviour for telemetry — losing the reading is worse than
reporting an unknown value. Flag it further up with `Enum.IsDefined` where it matters.

## Self-test

`Crc16Ccitt.SelfTest()` checks `crc("123456789") == 0x29B1`, the standard check vector.
Both consoles run it at startup; the decoder console exits without decoding if it fails.
Running the same check on the STM32 side confirms both ends use the same CRC variant.
