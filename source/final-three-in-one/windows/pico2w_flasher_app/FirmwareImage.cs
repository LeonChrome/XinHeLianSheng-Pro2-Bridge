using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace XinHeLianSheng.Pico2WFlasher;

public sealed class FirmwareImage
{
    private const int BlockSize = 512;
    private const uint MagicStart0 = 0x0A324655;
    private const uint MagicStart1 = 0x9E5D5157;
    private const uint MagicEnd = 0x0AB16F30;
    private const uint FamilyIdPresent = 0x00002000;
    private const uint ExtensionTagsPresent = 0x00008000;
    private const uint Rp2350ArmSecureFamily = 0xE48BFF59;
    private const uint Rp2350MetadataFamily = 0xE48BFF57;
    private const uint FlashBase = 0x10000000;
    private const uint FlashSize = 4 * 1024 * 1024;
    private const uint ReservedSettingsBytes = 10 * 4096;

    private FirmwareImage(byte[] bytes, string sha256, int mainFlashBlockCount,
        uint minimumAddress, uint maximumAddressExclusive)
    {
        Bytes = bytes;
        Sha256 = sha256;
        MainFlashBlockCount = mainFlashBlockCount;
        MinimumAddress = minimumAddress;
        MaximumAddressExclusive = maximumAddressExclusive;
    }

    public byte[] Bytes { get; }
    public string Sha256 { get; }
    public int MainFlashBlockCount { get; }
    public uint MinimumAddress { get; }
    public uint MaximumAddressExclusive { get; }

    public static FirmwareImage LoadEmbedded()
    {
        Assembly assembly = typeof(FirmwareImage).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(FirmwareManifest.ResourceName)
            ?? throw new InvalidDataException("EXE 内置固件资源缺失。");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Validate(memory.ToArray(), FirmwareManifest.ExpectedSha256);
    }

    public static FirmwareImage Validate(byte[] bytes, string? expectedSha256 = null)
    {
        if (bytes.Length == 0 || bytes.Length % BlockSize != 0)
            throw new InvalidDataException("UF2 文件长度不是完整的 512 字节块。");

        string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(expectedSha256) &&
            !sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("内置固件 SHA256 不匹配，程序文件可能损坏。");

        var mainBlocks = new HashSet<uint>();
        uint expectedMainTotal = 0;
        uint minimum = uint.MaxValue;
        uint maximum = 0;

        for (int offset = 0; offset < bytes.Length; offset += BlockSize)
        {
            ReadOnlySpan<byte> block = bytes.AsSpan(offset, BlockSize);
            if (ReadU32(block, 0) != MagicStart0 ||
                ReadU32(block, 4) != MagicStart1 ||
                ReadU32(block, 508) != MagicEnd)
                throw new InvalidDataException($"UF2 第 {offset / BlockSize} 块魔数无效。");

            uint flags = ReadU32(block, 8);
            uint target = ReadU32(block, 12);
            uint payloadSize = ReadU32(block, 16);
            uint blockNumber = ReadU32(block, 20);
            uint totalBlocks = ReadU32(block, 24);
            uint family = ReadU32(block, 28);

            if (payloadSize == 0 || payloadSize > 476)
                throw new InvalidDataException("UF2 包含无效负载长度。");
            if ((flags & FamilyIdPresent) == 0)
                throw new InvalidDataException("UF2 缺少 RP2350 family ID。");

            if (family == Rp2350ArmSecureFamily)
            {
                if (expectedMainTotal == 0) expectedMainTotal = totalBlocks;
                if (totalBlocks != expectedMainTotal || blockNumber >= totalBlocks ||
                    !mainBlocks.Add(blockNumber))
                    throw new InvalidDataException("UF2 主固件块编号不连续或重复。");

                ulong end = (ulong)target + payloadSize;
                uint settingsStart = FlashBase + FlashSize - ReservedSettingsBytes;
                if (target < FlashBase || end > settingsStart)
                    throw new InvalidDataException(
                        "固件写入范围越过程序区，可能破坏模式或蓝牙配对资料。");
                minimum = Math.Min(minimum, target);
                maximum = Math.Max(maximum, checked((uint)end));
            }
            else if (family != Rp2350MetadataFamily ||
                     (flags & ExtensionTagsPresent) == 0)
            {
                throw new InvalidDataException(
                    $"UF2 目标 family ID 不受支持：0x{family:X8}。");
            }
        }

        if (expectedMainTotal < 100 || mainBlocks.Count != expectedMainTotal)
            throw new InvalidDataException("UF2 主固件块不完整。");

        for (uint number = 0; number < expectedMainTotal; number++)
            if (!mainBlocks.Contains(number))
                throw new InvalidDataException($"UF2 缺少主固件块 {number}。");

        return new FirmwareImage(bytes, sha256, mainBlocks.Count, minimum, maximum);
    }

    private static uint ReadU32(ReadOnlySpan<byte> block, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(offset, 4));
}
