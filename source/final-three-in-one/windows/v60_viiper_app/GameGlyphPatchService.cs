using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Y700Switch2V60Viiper;

public enum GameGlyphPatchStatus { MissingGame, NotInstalled, Installed, Partial, Conflict, UnsupportedVersion, Busy, Failed }
public sealed record GameGlyphInstallation(string GameRoot, string DisplayName, string SteamAppId, string Source);
public sealed record GameGlyphPatchSnapshot(GameGlyphPatchStatus Status, string GameRoot, string Message,
    bool VersionMatches, bool GameRunning, bool CanInstall, bool CanRestore, string LogPath,
    string InstalledPatchVersion = "", string ExpectedPatchVersion = "");
public sealed record GameGlyphPatchResult(bool Success, bool RequiresElevation, string Message,
    GameGlyphPatchSnapshot Snapshot, string LogPath);
public sealed class GameGlyphPatchFile
{
    public string Name { get; set; } = "";
    public long Length { get; set; }
    public string Sha256 { get; set; } = "";
}
public sealed class GameGlyphFileSet
{
    public string Version { get; set; } = "";
    public List<GameGlyphPatchFile> Files { get; set; } = new();
}
public sealed class GameGlyphPatchManifest
{
    public string Version { get; set; } = "";
    public string TestedGameExeSha256 { get; set; } = "";
    public string TestedGameUtocSha256 { get; set; } = "";
    public List<GameGlyphPatchFile> Files { get; set; } = new();
    public List<GameGlyphFileSet> PreviousFileSets { get; set; } = new();
}

/// <summary>Small built-in display patch. Does not redistribute the extraction tool or its local parameters.</summary>
public sealed class GameGlyphPatchPackage
{
    public static readonly string[] FileNames = { "PRO2_PBZ_Glyphs_0_P.pak", "PRO2_PBZ_Glyphs_0_P.utoc", "PRO2_PBZ_Glyphs_0_P.ucas" };
    public GameGlyphPatchManifest Manifest { get; }
    private readonly Dictionary<string, byte[]> files;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    public GameGlyphPatchPackage(GameGlyphPatchManifest manifest, IReadOnlyDictionary<string, byte[]> contents)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version) || manifest.Version.Length > 100 ||
            manifest.PreviousFileSets.Any(p => string.IsNullOrWhiteSpace(p.Version) || p.Version.Length > 100))
            throw new InvalidDataException("图标补丁版本信息无效。");
        ValidateFileSet(manifest.Files);
        if (!IsHash(manifest.TestedGameExeSha256) || !IsHash(manifest.TestedGameUtocSha256))
            throw new InvalidDataException("图标补丁的游戏版本校验信息不完整。");
        foreach (var previous in manifest.PreviousFileSets) ValidateFileSet(previous.Files);
        files = new(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (!contents.TryGetValue(file.Name, out var bytes) || bytes.Length != file.Length ||
                !Convert.ToHexString(SHA256.HashData(bytes)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("内置图标补丁校验失败：" + file.Name);
            files.Add(file.Name, bytes.ToArray());
        }
        Manifest = manifest;
    }
    public byte[] GetFileBytes(string name) => files[name].ToArray();
    public static GameGlyphPatchPackage LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(GameGlyphPatchService).Assembly;
        using var stream = assembly.GetManifestResourceStream("Embedded.GameGlyph.PBZ.Manifest") ??
            throw new InvalidDataException("程序未包含游戏图标补丁清单。");
        if (stream.Length > 131072) throw new InvalidDataException("图标补丁清单过大。");
        var manifest = JsonSerializer.Deserialize<GameGlyphPatchManifest>(stream, JsonOptions) ?? throw new InvalidDataException("图标补丁清单无效。");
        var data = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        ValidateFileSet(manifest.Files);
        foreach (var file in manifest.Files)
        {
            using var input = assembly.GetManifestResourceStream("Embedded.GameGlyph.PBZ.File." + file.Name) ??
                throw new InvalidDataException("内置图标补丁文件缺失：" + file.Name);
            if (input.Length > 16777216) throw new InvalidDataException("内置图标补丁文件过大。");
            using var buffer = new MemoryStream(); input.CopyTo(buffer); data.Add(file.Name, buffer.ToArray());
        }
        return new(manifest, data);
    }
    internal IEnumerable<GameGlyphFileSet> KnownSets => new[] { new GameGlyphFileSet { Version = Manifest.Version, Files = Manifest.Files } }.Concat(Manifest.PreviousFileSets);
    internal bool IsKnownFile(string name, long length, string hash) => KnownSets.Any(set => set.Files.Any(f =>
        f.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && f.Length == length && f.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase)));
    private static bool IsHash(string value) => Regex.IsMatch(value, "\\A[0-9a-fA-F]{64}\\z");
    private static void ValidateFileSet(List<GameGlyphPatchFile> files)
    {
        if (files.Count != 3 || files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3 ||
            files.Any(f => !FileNames.Contains(f.Name, StringComparer.Ordinal) || f.Length is < 1 or > 16777216 || !IsHash(f.Sha256)))
            throw new InvalidDataException("图标补丁文件清单无效。");
    }
}

/// <summary>Version-gated, additive patch management, independent from BLE and virtual gamepads.</summary>
public sealed class GameGlyphPatchService
{
    private readonly GameGlyphPatchPackage package;
    private readonly string dataRoot;
    private readonly Func<string, bool> isGameRunning;
    private readonly Func<IEnumerable<string>> steamRoots;
    private static readonly JsonSerializerOptions WriteJson = new() { WriteIndented = true };
    public const string SteamAppId = "4115450";
    public GameGlyphPatchService(GameGlyphPatchPackage? package = null, string? dataRoot = null,
        Func<string, bool>? runningProbe = null, Func<IEnumerable<string>>? steamRoots = null)
    {
        this.package = package ?? GameGlyphPatchPackage.LoadEmbedded();
        this.dataRoot = Path.GetFullPath(dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PRO2WirelessReceiverControlBoard", "game_glyphs", "PBZ"));
        isGameRunning = runningProbe ?? IsGameRunning;
        this.steamRoots = steamRoots ?? ReadSteamRoots;
    }
    public Task<GameGlyphPatchSnapshot> InspectAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Inspect(path, cancellationToken), cancellationToken);
    public Task<GameGlyphPatchResult> ApplyAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Change(path, false, cancellationToken), cancellationToken);
    public Task<GameGlyphPatchResult> RestoreAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Change(path, true, cancellationToken), cancellationToken);

    public static string? NormalizeGameRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var candidate = Path.GetFullPath(path.Trim().Trim('"'));
            if (File.Exists(candidate)) candidate = Path.GetDirectoryName(candidate)!;
            for (int n = 0; n < 8 && !string.IsNullOrWhiteSpace(candidate); n++)
            {
                if (File.Exists(Path.Combine(candidate, "platform", "steam", "PBZ.exe")) &&
                    Directory.Exists(Path.Combine(candidate, "ProjectPBZ", "Content", "Paks")))
                    return Path.TrimEndingDirectorySeparator(candidate);
                candidate = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(candidate));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { }
        return null;
    }

    public IReadOnlyList<GameGlyphInstallation> DiscoverSteamInstallations()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in steamRoots())
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            try
            {
                roots.Add(Path.GetFullPath(root));
                var path = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(path)) path = Path.Combine(root, "config", "libraryfolders.vdf");
                if (File.Exists(path))
                {
                    var tree = ParseVdf(ReadSmallText(path));
                    foreach (var pair in Child(tree, "libraryfolders"))
                    {
                        string? library = pair.Value as string;
                        if (pair.Value is Dictionary<string, object> node) library = Value(node, "path");
                        if (!string.IsNullOrWhiteSpace(library) && Path.IsPathFullyQualified(library)) roots.Add(Path.GetFullPath(library));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException) { }
        }
        var found = new Dictionary<string, GameGlyphInstallation>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            string apps = Path.Combine(root, "steamapps");
            if (!Directory.Exists(apps)) continue;
            IEnumerable<string> manifests;
            try { manifests = Directory.EnumerateFiles(apps, "appmanifest_*.acf").OrderBy(p => Path.GetFileName(p).Equals("appmanifest_" + SteamAppId + ".acf", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var manifest in manifests)
            {
                try
                {
                    string text = ReadSmallText(manifest);
                    if (!Path.GetFileName(manifest).Equals("appmanifest_" + SteamAppId + ".acf", StringComparison.OrdinalIgnoreCase) &&
                        !text.Contains("Phantom Blade Zero", StringComparison.OrdinalIgnoreCase) &&
                        !text.Contains("\"PBZ\"", StringComparison.OrdinalIgnoreCase)) continue;
                    var app = Child(ParseVdf(text), "AppState");
                    var appid = Value(app, "appid"); var name = Value(app, "name"); var install = Value(app, "installdir");
                    if (appid != SteamAppId && !name.Contains("Phantom Blade Zero", StringComparison.OrdinalIgnoreCase) && !install.Equals("PBZ", StringComparison.OrdinalIgnoreCase)) continue;
                    if (install.Length == 0 || Path.IsPathRooted(install) || install.Split('/', '\\').Any(p => p is "." or "..")) continue;
                    string common = Path.GetFullPath(Path.Combine(apps, "common"));
                    string candidate = Path.GetFullPath(Path.Combine(common, install));
                    if (!candidate.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    var game = NormalizeGameRoot(candidate);
                    if (game != null) found[game] = new(game, string.IsNullOrWhiteSpace(name) ? "影之刃零" : name, appid, "Steam 游戏清单");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException) { }
            }
            foreach (var folder in new[] { "PBZ", "Phantom Blade Zero", "PhantomBladeZero" })
            {
                var game = NormalizeGameRoot(Path.Combine(apps, "common", folder));
                if (game != null && !found.ContainsKey(game)) found[game] = new(game, "影之刃零", "", "Steam 游戏目录");
            }
        }
        return found.Values.OrderBy(i => i.GameRoot, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private GameGlyphPatchSnapshot Inspect(string path, CancellationToken ct)
    {
        string? root = NormalizeGameRoot(path);
        if (root == null) return new(GameGlyphPatchStatus.MissingGame, path ?? "", "未找到《影之刃零》。请选择游戏目录或 PBZ.exe。", false, false, false, false, Path.Combine(dataRoot, "operations.log"));
        string target = Path.Combine(root, "ProjectPBZ", "Content", "Paks"); string log = LogPath(root);
        try
        {
            ct.ThrowIfCancellationRequested();
            var existing = ReadExisting(target, ct);
            bool conflict = existing.Any(f => !package.IsKnownFile(f.Name, f.Length, f.Sha256));
            bool running = isGameRunning(root);
            string version = package.KnownSets.FirstOrDefault(set => set.Files.All(f => existing.Any(e => Same(f, e))))?.Version ?? "";
            bool compatible = File.Exists(Path.Combine(target, "ProjectPBZ-Windows.utoc")) &&
                Hash(Path.Combine(root, "platform", "steam", "PBZ.exe"), ct).Equals(package.Manifest.TestedGameExeSha256, StringComparison.OrdinalIgnoreCase) &&
                Hash(Path.Combine(target, "ProjectPBZ-Windows.utoc"), ct).Equals(package.Manifest.TestedGameUtocSha256, StringComparison.OrdinalIgnoreCase);
            bool canRestore = existing.Count > 0 && !conflict && !running;
            bool canApply = compatible && !conflict && !running && (version != package.Manifest.Version || existing.Count != 3);
            if (conflict) return new(GameGlyphPatchStatus.Conflict, root, "同名补丁文件被其他内容占用或修改；不会覆盖或删除。", compatible, running, false, false, log, version, package.Manifest.Version);
            if (running) return new(GameGlyphPatchStatus.Busy, root, "请先正常退出游戏，再安装或还原图标。助手可以保持运行。", compatible, true, false, false, log, version, package.Manifest.Version);
            if (!compatible) return new(GameGlyphPatchStatus.UnsupportedVersion, root, existing.Count > 0 ? "游戏版本已变化；请先还原旧图标补丁，等待匹配版本。" : "此游戏版本尚未适配，暂不安装图标补丁。", false, false, false, canRestore, log, version, package.Manifest.Version);
            if (version.Length > 0) return new(GameGlyphPatchStatus.Installed, root, version == package.Manifest.Version ? "已使用 Pro2 按键图标。" : "检测到旧图标补丁，可以更新。", true, false, canApply, canRestore, log, version, package.Manifest.Version);
            if (existing.Count > 0) return new(GameGlyphPatchStatus.Partial, root, "图标补丁不完整，可以修复或还原。", true, false, true, true, log, "", package.Manifest.Version);
            return new(GameGlyphPatchStatus.NotInstalled, root, "可以为此游戏安装 Pro2 按键图标。", true, false, true, false, log, "", package.Manifest.Version);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { return new(GameGlyphPatchStatus.Failed, root, "无法检查游戏文件：" + ex.Message, false, false, false, false, log, "", package.Manifest.Version); }
    }

    private GameGlyphPatchResult Change(string path, bool restore, CancellationToken ct)
    {
        var before = Inspect(path, ct);
        if (!(restore ? before.CanRestore : before.CanInstall))
            return new(!restore && before.Status == GameGlyphPatchStatus.Installed && before.InstalledPatchVersion == package.Manifest.Version,
                false, before.Message, before, before.LogPath);
        string root = before.GameRoot, target = Path.Combine(root, "ProjectPBZ", "Content", "Paks");
        string op = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N");
        string stateDir = StateDirectory(root), backup = Path.Combine(stateDir, "backups", op);
        var written = new List<string>(); var deleted = new List<string>(); var staged = new List<string>();
        List<GameGlyphPatchFile> existing = new();
        try
        {
            Directory.CreateDirectory(stateDir);
            using var operationLock = new FileStream(Path.Combine(stateDir, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            // The actual installation is the lock scope. LocalAppData alone is
            // different when UAC uses a second administrator account or a Steam
            // library is reached through a junction. A kernel-owned temporary
            // lock in Paks serializes those callers without changing user files.
            using var installationLock = new FileStream(Path.Combine(target, ".pro2-glyph-operation.lock"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            before = Inspect(root, ct);
            if (!(restore ? before.CanRestore : before.CanInstall)) return new(false, false, before.Message, before, before.LogPath);
            existing = ReadExisting(target, ct);
            Directory.CreateDirectory(backup);
            foreach (var file in existing)
            {
                string saved = Path.Combine(backup, file.Name);
                File.Copy(Path.Combine(target, file.Name), saved, false);
                if (!Hash(saved, ct).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("修改前的图标备份校验失败，未继续操作。");
            }
            AtomicJson(Path.Combine(backup, "before.json"), new { Root = root, Operation = restore ? "Restore" : "Apply", Expected = package.Manifest.Version, Existing = existing, CreatedUtc = DateTimeOffset.UtcNow });
            Log(root, op, "begin", new { restore, version = package.Manifest.Version, existing = existing.Select(f => f.Name).ToArray(), originalArchivesOverwritten = false });
            if (!restore)
            {
                foreach (var file in package.Manifest.Files)
                {
                    string temp = Path.Combine(target, "." + file.Name + "." + op + ".tmp");
                    using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { staged.Add(temp); var bytes = package.GetFileBytes(file.Name); output.Write(bytes); output.Flush(true); }
                    if (!Hash(temp, ct).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("图标补丁临时文件校验失败。");
                }
            }
            ct.ThrowIfCancellationRequested();
            if (isGameRunning(root)) throw new IOException("游戏刚刚启动；本次未继续修改，请退出游戏后重试。");
            foreach (var file in existing)
            {
                string dest = Path.Combine(target, file.Name);
                if (!Hash(dest, ct).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("补丁文件在操作期间发生变化。");
                File.Delete(dest); deleted.Add(file.Name);
            }
            if (!restore)
            {
                for (int i = 0; i < package.Manifest.Files.Count; i++)
                {
                    var file = package.Manifest.Files[i]; string dest = Path.Combine(target, file.Name);
                    File.Move(staged[i], dest, false); written.Add(file.Name);
                    if (!Hash(dest, CancellationToken.None).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("图标补丁安装后校验失败。");
                }
            }
            AtomicJson(Path.Combine(stateDir, "state.json"), new { Root = root, Installed = !restore, PatchVersion = package.Manifest.Version,
                Backup = backup, Files = restore ? existing : package.Manifest.Files, CompletedUtc = DateTimeOffset.UtcNow, OriginalArchivesOverwritten = false });
            // Once the verified files and durable state are committed, failure
            // of the secondary diagnostic append must not undo a valid install
            // or leave state.json claiming a version that was rolled back.
            string logWarning = "";
            try { Log(root, op, "complete", new { restore, backup, version = package.Manifest.Version }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { logWarning = " 操作已完成，但日志追加失败；备份与状态记录已保留。"; }
            var after = Inspect(root, CancellationToken.None);
            return new(true, false, (restore ? "已还原游戏原有图标。" : "已安装 Pro2 按键图标，下次启动游戏生效。") + logWarning, after, after.LogPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
        {
            var rollbackErrors = new List<string>();
            foreach (string name in written)
            {
                try
                {
                    string dest = Path.Combine(target, name);
                    if (File.Exists(dest) && package.IsKnownFile(name, new FileInfo(dest).Length, Hash(dest, CancellationToken.None))) File.Delete(dest);
                }
                catch (Exception rollback) { rollbackErrors.Add(rollback.Message); }
            }
            foreach (string name in deleted)
            {
                try { string dest = Path.Combine(target, name); if (!File.Exists(dest)) File.Copy(Path.Combine(backup, name), dest, false); }
                catch (Exception rollback) { rollbackErrors.Add(rollback.Message); }
            }
            try { Log(root, op, "failed", new { error = ex.Message, backup, rollbackErrors }); } catch { }
            bool elevation = ex is UnauthorizedAccessException;
            string message = elevation ? "游戏目录需要管理员权限。请授权后重试。" : "图标操作未完成：" + ex.Message;
            if (rollbackErrors.Count > 0) message += " 部分恢复失败，备份位于：" + backup;
            var snapshot = Inspect(root, CancellationToken.None);
            return new(false, elevation && rollbackErrors.Count == 0, message, snapshot, before.LogPath);
        }
        finally
        {
            foreach (var temp in staged) { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }
    }

    private List<GameGlyphPatchFile> ReadExisting(string target, CancellationToken ct)
    {
        var result = new List<GameGlyphPatchFile>();
        foreach (var name in GameGlyphPatchPackage.FileNames)
        {
            string path = Path.Combine(target, name);
            if (Directory.Exists(path)) throw new InvalidDataException("补丁文件名被同名目录占用。");
            if (!File.Exists(path)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("补丁文件是链接，暂不修改。");
            result.Add(new() { Name = name, Length = new FileInfo(path).Length, Sha256 = Hash(path, ct) });
        }
        return result;
    }
    private static bool Same(GameGlyphPatchFile a, GameGlyphPatchFile b) => a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase) && a.Length == b.Length && a.Sha256.Equals(b.Sha256, StringComparison.OrdinalIgnoreCase);
    private static string Hash(string path, CancellationToken ct)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[131072]; int n;
        while ((n = input.Read(buffer)) > 0) { ct.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, n); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private string StateDirectory(string root) => Path.Combine(dataRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..24]);
    private string LogPath(string root) => Path.Combine(StateDirectory(root), "operations.log");
    private void Log(string root, string operation, string stage, object details)
    {
        string path = LogPath(root); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, operation, stage, gameRoot = root, details }) + Environment.NewLine, new UTF8Encoding(false));
    }
    private static void AtomicJson(string path, object value)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value, WriteJson), new UTF8Encoding(false)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static bool IsGameRunning(string root)
    {
        foreach (var process in Process.GetProcessesByName("PBZ"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path == null || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { return true; } // An inaccessible PBZ process is safer to treat as running.
            }
        }
        return false;
    }
    private static IEnumerable<string> ReadSteamRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using var registry = RegistryKey.OpenBaseKey(hive, view); using var key = registry.OpenSubKey(@"SOFTWARE\Valve\Steam");
                    foreach (string name in new[] { "SteamPath", "InstallPath", "SteamExe" })
                    { if (key?.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value)) roots.Add(name == "SteamExe" ? Path.GetDirectoryName(value)! : value); }
                }
                catch { }
            }
        }
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        { var path = Path.Combine(Environment.GetFolderPath(folder), "Steam"); if (Directory.Exists(path)) roots.Add(path); }
        return roots;
    }
    private static string ReadSmallText(string path)
    { if (new FileInfo(path).Length > 4194304) throw new InvalidDataException("Steam 清单过大。"); return File.ReadAllText(path); }
    private static string Value(Dictionary<string, object> node, string key) => node.TryGetValue(key, out var value) ? value as string ?? "" : "";
    private static Dictionary<string, object> Child(Dictionary<string, object> node, string key) => node.TryGetValue(key, out var value) && value is Dictionary<string, object> child ? child : new(StringComparer.OrdinalIgnoreCase);
    internal static Dictionary<string, object> ParseVdf(string text)
    {
        var tokens = Regex.Matches(text, "//[^\\r\\n]*|\"((?:\\\\.|[^\"\\\\])*)\"|[{}]")
            .Cast<Match>().Where(m => !m.Value.StartsWith("//", StringComparison.Ordinal)).ToArray();
        int at = 0;
        Dictionary<string, object> Parse(int depth)
        {
            if (depth > 16) throw new InvalidDataException("Steam 清单嵌套过深。");
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (at < tokens.Length)
            {
                if (tokens[at].Value == "}") { at++; return result; }
                if (!tokens[at].Value.StartsWith('"')) throw new InvalidDataException("Steam 清单格式错误。");
                string key = Unescape(tokens[at++].Groups[1].Value);
                if (at >= tokens.Length) throw new InvalidDataException("Steam 清单值缺失。");
                if (tokens[at].Value == "{") { at++; result[key] = Parse(depth + 1); }
                else if (tokens[at].Value.StartsWith('"')) result[key] = Unescape(tokens[at++].Groups[1].Value);
                else throw new InvalidDataException("Steam 清单值无效。");
            }
            return result;
        }
        return Parse(0);
    }
    private static string Unescape(string value) => value.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
}
