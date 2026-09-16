using System.IO;
using System.Security.Cryptography;

namespace XinHeLianSheng.ControlCenter;

public static class EmbeddedBackendStore
{
    public static string? RootOverride { get; set; }

    public static string RootDirectory => RootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XinHeLianSheng", "ControlCenter", "backends");

    public static async Task<string> EnsureExtractedAsync(
        BackendDescriptor backend, CancellationToken cancellationToken = default)
    {
        PayloadManifest manifest = PayloadBundle.ReadManifest();
        PayloadEntry entry = manifest.Entries.SingleOrDefault(
            value => value.Key == backend.PayloadKey)
            ?? throw new InvalidDataException("打包目录缺少后端：" + backend.PayloadKey);
        if (!string.Equals(entry.Sha256, backend.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("后端目录与程序版本清单不一致：" + backend.Key);
        }

        string directory = Path.Combine(RootDirectory, backend.Key);
        string target = Path.Combine(directory, backend.FileName);
        Directory.CreateDirectory(directory);
        if (File.Exists(target) &&
            string.Equals(await ComputeSha256Async(target, cancellationToken),
                backend.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return target;
        }
        return await PayloadBundle.ExtractAsync(entry, target, cancellationToken);
    }

    public static async Task<string> ComputeSha256Async(
        string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
