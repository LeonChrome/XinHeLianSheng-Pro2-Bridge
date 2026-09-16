using System.IO;
using System.Text;
using System.Text.Json;

namespace XinHeLianSheng.ControlCenter;

public sealed record PayloadEntry(string Key, long Offset, long Length, string Sha256);

public sealed record PayloadManifest(int Format, IReadOnlyList<PayloadEntry> Entries);

public static class PayloadBundle
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("XHLS-PAYLOAD-v1!");

    public static PayloadManifest ReadManifest()
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定控制中心 EXE 路径。");
        using var stream = new FileStream(executable, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        if (stream.Length < Magic.Length + sizeof(long))
        {
            throw new InvalidDataException("当前 EXE 没有三合一后端数据。请使用已打包版本。");
        }

        stream.Seek(-(Magic.Length + sizeof(long)), SeekOrigin.End);
        Span<byte> lengthBytes = stackalloc byte[sizeof(long)];
        stream.ReadExactly(lengthBytes);
        long manifestLength = BitConverter.ToInt64(lengthBytes);
        byte[] magic = new byte[Magic.Length];
        stream.ReadExactly(magic);
        if (!magic.AsSpan().SequenceEqual(Magic) || manifestLength <= 0 ||
            manifestLength > 1024 * 1024 ||
            manifestLength > stream.Length - Magic.Length - sizeof(long))
        {
            throw new InvalidDataException("三合一后端目录签名无效或已损坏。");
        }

        long manifestOffset = stream.Length - Magic.Length - sizeof(long) - manifestLength;
        stream.Seek(manifestOffset, SeekOrigin.Begin);
        byte[] json = new byte[manifestLength];
        stream.ReadExactly(json);
        PayloadManifest manifest = JsonSerializer.Deserialize<PayloadManifest>(json)
            ?? throw new InvalidDataException("三合一后端目录无法解析。");
        if (manifest.Format != 1 || manifest.Entries.Count == 0)
        {
            throw new InvalidDataException("三合一后端目录版本不受支持。");
        }
        foreach (PayloadEntry entry in manifest.Entries)
        {
            if (entry.Offset < 0 || entry.Length <= 0 ||
                entry.Offset + entry.Length > manifestOffset)
            {
                throw new InvalidDataException("后端范围越界：" + entry.Key);
            }
        }
        return manifest;
    }

    public static async Task<string> ExtractAsync(
        PayloadEntry entry, string target, CancellationToken cancellationToken)
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定控制中心 EXE 路径。");
        string temporary = target + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        await using var input = new FileStream(executable, FileMode.Open,
            FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        input.Seek(entry.Offset, SeekOrigin.Begin);
        await using (var output = new FileStream(temporary, FileMode.Create,
                         FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
        {
            byte[] buffer = new byte[1024 * 1024];
            long remaining = entry.Length;
            while (remaining > 0)
            {
                int wanted = (int)Math.Min(buffer.Length, remaining);
                int read = await input.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken);
                if (read == 0)
                {
                    throw new EndOfStreamException("后端数据提前结束：" + entry.Key);
                }
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
            await output.FlushAsync(cancellationToken);
        }

        string actual = await EmbeddedBackendStore.ComputeSha256Async(
            temporary, cancellationToken);
        if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temporary);
            throw new InvalidDataException(
                $"后端数据校验失败：{entry.Key} expected={entry.Sha256} actual={actual}");
        }
        File.Move(temporary, target, overwrite: true);
        return target;
    }
}
