using System.IO;

namespace XinHeLianSheng.Pico2WFlasher;

public sealed record FlashWriteResult(bool DeviceRebootedDuringFinalFlush, long BytesWritten);

public static class FirmwareFlasher
{
    public static async Task<FlashWriteResult> WriteAsync(
        BootselDevice device,
        FirmwareImage image,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(device.RootPath);
        string target = Path.GetFullPath(Path.Combine(root, FirmwareManifest.TargetFileName));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拒绝写入 BOOTSEL 磁盘之外的路径。");

        var drive = new DriveInfo(Path.GetPathRoot(root)!);
        if (!drive.IsReady || drive.AvailableFreeSpace < image.Bytes.Length + 64 * 1024)
            throw new IOException("BOOTSEL 磁盘空间不足或已经断开。");

        long written = 0;
        bool rebootedDuringFlush = false;
        try
        {
            await using var stream = new FileStream(
                target,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);

            const int chunkSize = 64 * 1024;
            while (written < image.Bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(chunkSize, image.Bytes.Length - written);
                await stream.WriteAsync(
                    image.Bytes.AsMemory((int)written, count), cancellationToken);
                written += count;
                progress?.Report((double)written / image.Bytes.Length);
            }
            await stream.FlushAsync(cancellationToken);
            stream.Flush(true);
        }
        catch (IOException) when (written == image.Bytes.Length && !Directory.Exists(root))
        {
            // RP2350 resets and removes the virtual disk immediately after it has
            // accepted the final UF2 block. Windows can surface that expected
            // transition as an I/O error even though the entire image was sent.
            rebootedDuringFlush = true;
        }

        if (written != image.Bytes.Length)
            throw new IOException($"固件仅写入 {written}/{image.Bytes.Length} 字节，不能判定成功。");
        return new FlashWriteResult(rebootedDuringFlush, written);
    }
}
