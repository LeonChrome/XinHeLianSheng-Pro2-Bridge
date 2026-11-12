using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Y700Switch2V60Viiper;

public sealed record GameGlyphCompatibilityResult(bool Compatible, string Detail);

/// <summary>Read-only IOStore identity check for additive display overlays.
/// No archive decryption, executable patching, offsets into the game, or whole
/// game build allowlist. Matching identities is not proof of in-game rendering.</summary>
public static class GameGlyphCompatibility
{
    public static GameGlyphCompatibilityResult Check(string targetDirectory, byte[] overlayToc, CancellationToken token)
    {
        try
        {
            using var overlay = new MemoryStream(overlayToc, false);
            var required = ReadChunks(overlay, token);
            // Type 1 is ExportBundleData. The overlay's container header is new,
            // so must never be required in the original game's TOC.
            var targets = required.Chunks.Where(id => id.EndsWith("01", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
            if (targets.Count == 0) return new(false, "图标补丁没有可验证的资源标识。");
            var found = new HashSet<string>(StringComparer.Ordinal);
            int containers = 0;
            foreach (string path in Directory.EnumerateFiles(targetDirectory, "*.utoc").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Take(128))
            {
                token.ThrowIfCancellationRequested();
                string name = Path.GetFileName(path);
                // An old overlay must not certify itself or mask missing assets.
                if (name.StartsWith("PRO2_", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("_P.", StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(Path.ChangeExtension(path, ".ucas"))) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var current = ReadChunks(stream, token, targets);
                if (current.Version != required.Version) continue;
                containers++;
                foreach (string id in current.Chunks) if (targets.Contains(id)) found.Add(id);
            }
            int missing = targets.Count - found.Count;
            return missing == 0
                ? new(true, $"资源标识匹配 {found.Count}/{targets.Count}；支持游戏文件变化。更新后的游戏尚需实际确认显示效果，异常可还原。")
                : new(false, $"图标资源标识匹配 {found.Count}/{targets.Count}，缺少 {missing} 项；容器格式匹配 {containers} 个。暂不套用旧资源。请选择完整游戏目录。");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OverflowException)
        { return new(false, "无法验证图标资源结构：" + ex.Message); }
    }

    internal static (byte Version, HashSet<string> Chunks) ReadChunks(Stream stream, CancellationToken token, IReadOnlySet<string>? wanted = null)
    {
        Span<byte> header = stackalloc byte[144]; stream.ReadExactly(header);
        if (!header[..16].SequenceEqual(Encoding.ASCII.GetBytes("-==--==--==--==-"))) throw new InvalidDataException("不是 IOStore 索引。");
        byte version = header[16];
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
        if (version is < 3 or > 8 || headerSize is < 144 or > 4096 || count is 0 or > 2000000 ||
            headerSize + (long)count * 22 > stream.Length) throw new InvalidDataException("索引头或资源数量异常。");
        stream.Position = headerSize;
        byte[] buffer = new byte[12 * 1024];
        var chunks = new HashSet<string>(StringComparer.Ordinal);
        var wantedKeys = wanted?.Select(id => Convert.FromHexString(id)).Select(id =>
            (BinaryPrimitives.ReadUInt64LittleEndian(id), BinaryPrimitives.ReadUInt32LittleEndian(id.AsSpan(8)))).ToHashSet();
        uint remaining = count;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            int batch = (int)Math.Min(remaining, 1024u), bytes = batch * 12;
            stream.ReadExactly(buffer.AsSpan(0, bytes));
            for (int i = 0; i < batch; i++)
            {
                var id = buffer.AsSpan(i * 12, 12);
                if (wantedKeys == null || wantedKeys.Contains((BinaryPrimitives.ReadUInt64LittleEndian(id), BinaryPrimitives.ReadUInt32LittleEndian(id[8..]))))
                    chunks.Add(Convert.ToHexString(id));
            }
            remaining -= (uint)batch;
        }
        return (version, chunks);
    }
}
