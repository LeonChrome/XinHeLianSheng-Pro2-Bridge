using System.Buffers.Binary;
using System.Security.Cryptography;
using XinHeLianSheng.Pico2WFlasher;

int failures = 0;

void Check(bool condition, string name)
{
    Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
    if (!condition) failures++;
}

void ExpectInvalid(Action action, string name)
{
    try
    {
        action();
        Check(false, name);
    }
    catch (InvalidDataException)
    {
        Check(true, name);
    }
}

FirmwareImage image = FirmwareImage.LoadEmbedded();
Check(image.Sha256 == FirmwareManifest.ExpectedSha256,
    "embedded UF2 SHA256 is pinned");
Check(image.MainFlashBlockCount >= 2012,
    "embedded UF2 main RP2350 stream is complete and includes current payload");
Check(image.MaximumAddressExclusive < 0x103F6000,
    "embedded UF2 preserves mode and Bluetooth storage sectors");

string readyIdentity =
    $"[CDC_CMD] firmware_revision={FirmwareManifest.Revision} " +
    $"firmware_version={FirmwareManifest.Version} board=pico2_w " +
    "target=rp2350 runtime_ready=true";
Check(FirmwareIdentity.IsExpectedAndReady(readyIdentity, out _),
    "post-flash identity requires the expected ready runtime");
Check(!FirmwareIdentity.IsExpectedAndReady(
        readyIdentity.Replace("runtime_ready=true", "runtime_ready=false"), out _),
    "post-flash identity rejects an incomplete startup");
Check(!FirmwareIdentity.IsExpectedAndReady(
        readyIdentity.Replace(
            $"revision={FirmwareManifest.Revision}", "revision=r0"), out _),
    "post-flash identity rejects an older revision");

byte[] badMagic = (byte[])image.Bytes.Clone();
badMagic[0] ^= 0x01;
ExpectInvalid(() => FirmwareImage.Validate(badMagic),
    "corrupted UF2 magic is rejected");

byte[] truncated = image.Bytes[..^1];
ExpectInvalid(() => FirmwareImage.Validate(truncated),
    "truncated UF2 is rejected");

ExpectInvalid(() => FirmwareImage.Validate(image.Bytes, new string('0', 64)),
    "unexpected UF2 SHA256 is rejected");

int firstMainOffset = FindMainBlock(image.Bytes, 0);
byte[] wrongFamily = (byte[])image.Bytes.Clone();
BinaryPrimitives.WriteUInt32LittleEndian(
    wrongFamily.AsSpan(firstMainOffset + 28, 4), 0x12345678);
ExpectInvalid(() => FirmwareImage.Validate(wrongFamily),
    "non-RP2350 firmware family is rejected");

byte[] settingsOverwrite = (byte[])image.Bytes.Clone();
BinaryPrimitives.WriteUInt32LittleEndian(
    settingsOverwrite.AsSpan(firstMainOffset + 12, 4), 0x103F6000);
ExpectInvalid(() => FirmwareImage.Validate(settingsOverwrite),
    "UF2 that overwrites saved mode or bonds is rejected");

int secondMainOffset = FindMainBlock(image.Bytes, 1);
byte[] duplicateBlock = (byte[])image.Bytes.Clone();
BinaryPrimitives.WriteUInt32LittleEndian(
    duplicateBlock.AsSpan(secondMainOffset + 20, 4), 0);
ExpectInvalid(() => FirmwareImage.Validate(duplicateBlock),
    "duplicate UF2 block number is rejected");

const string rp2350Info =
    "UF2 Bootloader v3.0\nModel: Raspberry Pi RP2350\nBoard-ID: RP2350-E15\n";
BootselScanResult pico2 = BootselDeviceLocator.InspectForTest(
    "R:\\", "RP2350", rp2350Info);
Check(pico2.Supported.Count == 1 && pico2.Unsupported.Count == 0,
    "RP2350 BOOTSEL volume is accepted");

BootselScanResult pico1 = BootselDeviceLocator.InspectForTest(
    "R:\\", "RPI-RP2",
    "UF2 Bootloader v3.0\nModel: Raspberry Pi RP2\nBoard-ID: RPI-RP2\n");
Check(pico1.Supported.Count == 0 && pico1.Unsupported.Count == 1,
    "RP2040 Pico 1 BOOTSEL volume is rejected");

BootselScanResult fake = BootselDeviceLocator.InspectForTest(
    "R:\\", "RP2350", "Model: ordinary removable disk\n");
Check(fake.Supported.Count == 0 && fake.Unsupported.Count == 1,
    "look-alike removable disk without UF2 bootloader is rejected");

string temp = Path.Combine(Path.GetTempPath(), "xhls-pico-flasher-test-" + Guid.NewGuid());
Directory.CreateDirectory(temp);
try
{
    var target = new BootselDevice(temp + Path.DirectorySeparatorChar,
        "RP2350", "Raspberry Pi RP2350", "RP2350-E15", rp2350Info);
    FlashWriteResult write = await FirmwareFlasher.WriteAsync(
        target, image, null, CancellationToken.None);
    string output = Path.Combine(temp, FirmwareManifest.TargetFileName);
    string outputHash = Convert.ToHexString(
        SHA256.HashData(await File.ReadAllBytesAsync(output))).ToLowerInvariant();
    Check(write.BytesWritten == image.Bytes.Length && outputHash == image.Sha256,
        "flash writer emits every byte without mutation");
}
finally
{
    Directory.Delete(temp, true);
}

Console.WriteLine($"[RESULT] failures={failures}");
return failures == 0 ? 0 : 1;

static int FindMainBlock(byte[] bytes, uint blockNumber)
{
    for (int offset = 0; offset < bytes.Length; offset += 512)
    {
        uint family = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 28, 4));
        uint number = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 20, 4));
        if (family == 0xE48BFF59 && number == blockNumber) return offset;
    }
    throw new InvalidDataException("main block not found");
}
