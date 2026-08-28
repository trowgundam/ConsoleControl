using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

const uint MagicStart0 = 0x0A324655;
const uint MagicStart1 = 0x9E5D5157;
const uint MagicEnd = 0x0AB16F30;
const uint FamilyIdPresent = 0x00002000;
const uint Nrf52840FamilyId = 0xADA52840;
const int BlockSize = 512;
const int PayloadSize = 256;

if (args.Length == 0)
{
    Usage();
    return 2;
}

try
{
    return args[0] switch
    {
        "inspect-mount" when args.Length == 2 => InspectMount(args[1]),
        "inspect-uf2" when args.Length == 2 => InspectUf2(args[1], null, null),
        "verify-uf2" when args.Length == 4 => InspectUf2(args[1], ParseAddress(args[2]), ParseAddress(args[3])),
        "pack-uf2" when args.Length == 5 => PackUf2(
            args[1], args[2], ParseAddress(args[3]), ParseAddress(args[4])),
        "verify-switch-pro" when args.Length == 2 => VerifySwitchPro(args[1]),
        _ => Usage()
    };
}
catch (Exception exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}

static int InspectMount(string mountPath)
{
    string infoPath = Path.Combine(mountPath, "INFO_UF2.TXT");
    string currentPath = Path.Combine(mountPath, "CURRENT.UF2");
    Console.Write(File.ReadAllText(infoPath));
    Console.WriteLine($"INFO_UF2.TXT sha256: {Hash(infoPath)}");
    Console.WriteLine($"CURRENT.UF2 sha256: {Hash(currentPath)}");
    return InspectUf2(currentPath, null, null);
}

static int InspectUf2(string path, uint? allowedStart, uint? allowedEnd)
{
    using FileStream stream = File.OpenRead(path);
    if (stream.Length == 0 || stream.Length % BlockSize != 0)
    {
        throw new InvalidDataException("UF2 length must be a non-zero multiple of 512 bytes");
    }

    byte[] block = new byte[BlockSize];
    uint minimum = uint.MaxValue;
    uint maximum = 0;
    uint? expectedBlocks = null;
    uint? family = null;
    int blocksRead = 0;

    while (stream.ReadExactlyOrEnd(block))
    {
        ValidateBlock(block, (uint)blocksRead, ref expectedBlocks, ref family);
        uint address = ReadUInt32(block, 12);
        uint payloadLength = ReadUInt32(block, 16);
        uint end = checked(address + payloadLength);
        minimum = Math.Min(minimum, address);
        maximum = Math.Max(maximum, end);
        blocksRead++;
    }

    if (expectedBlocks != blocksRead)
    {
        throw new InvalidDataException($"header expects {expectedBlocks} blocks but file contains {blocksRead}");
    }

    if (allowedStart is not null && minimum < allowedStart)
    {
        throw new InvalidDataException($"image starts at 0x{minimum:X8}, below allowed 0x{allowedStart:X8}");
    }

    if (allowedEnd is not null && maximum > allowedEnd)
    {
        throw new InvalidDataException($"image ends at 0x{maximum:X8}, above allowed 0x{allowedEnd:X8}");
    }

    Console.WriteLine(
        $"blocks={blocksRead} address_start=0x{minimum:X8} address_end_exclusive=0x{maximum:X8} family=0x{family:X8}");
    return 0;
}

static int PackUf2(string binaryPath, string outputPath, uint baseAddress, uint allowedEnd)
{
    byte[] binary = File.ReadAllBytes(binaryPath);
    if (binary.Length == 0)
    {
        throw new InvalidDataException("binary must not be empty");
    }

    uint blockCount = checked((uint)((binary.Length + PayloadSize - 1) / PayloadSize));
    uint packedEnd = checked(baseAddress + blockCount * PayloadSize);
    if (baseAddress < 0x00026000 || packedEnd > allowedEnd || allowedEnd > 0x000F4000)
    {
        throw new InvalidDataException(
            $"packed binary range 0x{baseAddress:X8}..0x{packedEnd:X8} is outside the verified application region");
    }

    using (FileStream output = File.Create(outputPath))
    {
        byte[] block = new byte[BlockSize];

        for (uint blockNumber = 0; blockNumber < blockCount; blockNumber++)
        {
            block.AsSpan().Clear();
            int inputOffset = checked((int)blockNumber * PayloadSize);
            int length = Math.Min(PayloadSize, binary.Length - inputOffset);
            WriteUInt32(block, 0, MagicStart0);
            WriteUInt32(block, 4, MagicStart1);
            WriteUInt32(block, 8, FamilyIdPresent);
            WriteUInt32(block, 12, checked(baseAddress + blockNumber * PayloadSize));
            WriteUInt32(block, 16, PayloadSize);
            WriteUInt32(block, 20, blockNumber);
            WriteUInt32(block, 24, blockCount);
            WriteUInt32(block, 28, Nrf52840FamilyId);
            binary.AsSpan(inputOffset, length).CopyTo(block.AsSpan(32, length));
            WriteUInt32(block, 508, MagicEnd);
            output.Write(block);
        }
    }

    Console.WriteLine(
        $"packed {binary.Length} bytes into {blockCount} UF2 blocks at 0x{baseAddress:X8}: {outputPath}");
    return InspectUf2(outputPath, baseAddress, allowedEnd);
}

static int VerifySwitchPro(string hidrawPath)
{
    using FileStream device = new(
        hidrawPath,
        FileMode.Open,
        FileAccess.ReadWrite,
        FileShare.ReadWrite,
        64,
        FileOptions.Asynchronous);

    VerifyUsbCommand(device, 0x02, [0x81, 0x02]);
    VerifyUsbCommand(device, 0x03, [0x81, 0x03]);
    VerifyUsbCommand(device, 0x02, [0x81, 0x02]);
    SendUsbCommand(device, 0x04);

    byte[] deviceInfo = SendSubcommand(device, 0x02);
    Require(deviceInfo[13] == 0x82 && deviceInfo[14] == 0x02,
        "device-info reply has the wrong ACK or echoed subcommand");
    Require(deviceInfo[15] == 0x03 && deviceInfo[17] == 0x03,
        "device-info reply does not identify a Pro Controller");
    Console.WriteLine("subcommand 02: Pro Controller device info accepted");

    byte[] pairing = SendSubcommand(
        device, 0x01, [0x04, 0x0f, 0xbb, 0xfd, 0xab, 0xa9, 0x3c]);
    Require(pairing[13] == 0x81 && pairing[14] == 0x01 && pairing[15] == 0x03,
        "Switch 2 pairing-finalization reply has the wrong ACK or body type");
    Console.WriteLine("subcommand 01: Switch 2 pairing finalization accepted");

    byte[] calibration = SendSubcommand(device, 0x10, [0x3d, 0x60, 0x00, 0x00, 0x12]);
    Require(calibration[13] == 0x90 && calibration[14] == 0x10,
        "SPI-read reply has the wrong ACK or echoed subcommand");
    Require(calibration[19] == 0x12,
        "SPI-read reply does not echo the requested length");
    Console.WriteLine("subcommand 10: factory stick calibration accepted");

    byte[] mode = SendSubcommand(device, 0x03, [0x30]);
    Require(mode[13] == 0x80 && mode[14] == 0x03,
        "report-mode reply has the wrong ACK or echoed subcommand");
    byte[] state = ReadMatching(device, report => report[0] == 0x30, "input report 30");
    Require(state.Length == 64 && state[2] == 0x91,
        "input report 30 has the wrong length or battery/connection byte");
    Console.WriteLine("subcommand 03: report 30 stream started");
    Console.WriteLine("Switch Pro USB handshake passed");
    return 0;
}

static void VerifyUsbCommand(FileStream device, byte command, byte[] prefix)
{
    SendUsbCommand(device, command);
    byte[] reply = ReadMatching(
        device,
        report => report.AsSpan().StartsWith(prefix),
        $"USB response {command:X2}");
    Require(reply.AsSpan().StartsWith(prefix), $"USB response {command:X2} did not match");
    Console.WriteLine($"USB command {command:X2}: accepted");
}

static void SendUsbCommand(FileStream device, byte command)
{
    byte[] report = new byte[64];
    report[0] = 0x80;
    report[1] = command;
    device.Write(report);
    device.Flush();
}

static byte[] SendSubcommand(FileStream device, byte subcommand, byte[]? arguments = null)
{
    byte[] report = new byte[64];
    report[0] = 0x01;
    // Neutral rumble framing used by Nintendo hosts.
    byte[] neutralRumble = [0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40];
    neutralRumble.CopyTo(report, 2);
    report[10] = subcommand;
    arguments?.CopyTo(report, 11);
    device.Write(report);
    device.Flush();
    return ReadMatching(
        device,
        input => input[0] == 0x21 && input[14] == subcommand,
        $"subcommand response {subcommand:X2}");
}

static byte[] ReadMatching(FileStream device, Func<byte[], bool> matches, string description)
{
    DateTime deadline = DateTime.UtcNow.AddSeconds(3);
    while (DateTime.UtcNow < deadline)
    {
        byte[] report = new byte[64];
        TimeSpan remaining = deadline - DateTime.UtcNow;
        try
        {
            int read = device.ReadAsync(report)
                .AsTask()
                .WaitAsync(remaining)
                .GetAwaiter()
                .GetResult();
            if (read == 0)
            {
                throw new EndOfStreamException("HID device closed while waiting for a report");
            }
            if (read != report.Length)
            {
                throw new InvalidDataException($"HID report length was {read}, expected {report.Length}");
            }
            if (matches(report))
            {
                return report;
            }
        }
        catch (TimeoutException)
        {
            break;
        }
    }
    throw new TimeoutException($"timed out waiting for {description}");
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidDataException(message);
    }
}

static void ValidateBlock(
    byte[] block,
    uint expectedNumber,
    ref uint? expectedBlocks,
    ref uint? family)
{
    if (ReadUInt32(block, 0) != MagicStart0
        || ReadUInt32(block, 4) != MagicStart1
        || ReadUInt32(block, 508) != MagicEnd)
    {
        throw new InvalidDataException($"block {expectedNumber} has invalid UF2 magic");
    }

    if ((ReadUInt32(block, 8) & FamilyIdPresent) == 0)
    {
        throw new InvalidDataException($"block {expectedNumber} has no family ID");
    }

    uint number = ReadUInt32(block, 20);
    uint count = ReadUInt32(block, 24);
    uint blockFamily = ReadUInt32(block, 28);
    if (number != expectedNumber)
    {
        throw new InvalidDataException($"expected block {expectedNumber}, found {number}");
    }

    expectedBlocks ??= count;
    family ??= blockFamily;
    if (expectedBlocks != count || family != blockFamily)
    {
        throw new InvalidDataException("UF2 block metadata is inconsistent");
    }
}

static string Hash(string path)
{
    using FileStream stream = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(stream));
}

static uint ParseAddress(string value)
{
    string digits = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
    return uint.Parse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
}

static uint ReadUInt32(byte[] bytes, int offset) =>
    BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)));

static void WriteUInt32(byte[] bytes, int offset, uint value) =>
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)), value);

static int Usage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  ConsoleControl.DeviceProbe inspect-mount <mount-path>");
    Console.Error.WriteLine("  ConsoleControl.DeviceProbe inspect-uf2 <uf2-path>");
    Console.Error.WriteLine("  ConsoleControl.DeviceProbe verify-uf2 <uf2-path> <allowed-start> <allowed-end>");
    Console.Error.WriteLine("  ConsoleControl.DeviceProbe pack-uf2 <bin-path> <uf2-path> <base> <allowed-end>");
    Console.Error.WriteLine("  ConsoleControl.DeviceProbe verify-switch-pro <hidraw-path>");
    return 2;
}

static class StreamExtensions
{
    public static bool ReadExactlyOrEnd(this Stream stream, byte[] buffer)
    {
        int read = stream.Read(buffer, 0, buffer.Length);
        if (read == 0)
        {
            return false;
        }

        stream.ReadExactly(buffer.AsSpan(read));
        return true;
    }
}
