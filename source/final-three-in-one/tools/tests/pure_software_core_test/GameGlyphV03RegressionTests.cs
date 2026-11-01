using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Y700Switch2V60Viiper;

internal static class GameGlyphV03RegressionTests
{
    public static void Run(Action<bool, string> check, string output)
    {
        var embedded = GameGlyphPatchPackage.LoadEmbedded();
        check(embedded.Manifest.Version == "0.3.0" && embedded.Manifest.PreviousFileSets.Any(p => p.Version == "0.2.0") &&
            embedded.Manifest.PreviousFileSets.Any(p => p.Version == "0.1.0"), "glyph v0.3 recognizes both previous file sets");
        using var manifestStream = typeof(GameGlyphPatchPackage).Assembly.GetManifestResourceStream("Embedded.GameGlyph.PBZ.Manifest")!;
        using var doc = JsonDocument.Parse(manifestStream);
        check(doc.RootElement.GetProperty("offlineRoundtrip").GetArrayLength() == 18,
            "glyph package has eighteen independently readback-verified assets");
        var root = Path.Combine(output, "glyph-upgrade 中文 空格");
        var game = Path.Combine(root, "Game"); var paks = Path.Combine(game, "ProjectPBZ", "Content", "Paks");
        Directory.CreateDirectory(paks); Directory.CreateDirectory(Path.Combine(game, "platform", "steam"));
        var exeBytes = Encoding.UTF8.GetBytes("test executable" + Guid.NewGuid());
        var tocBytes = Encoding.UTF8.GetBytes("test toc" + Guid.NewGuid());
        var exe = Path.Combine(game, "platform", "steam", "PBZ.exe");
        var toc = Path.Combine(paks, "ProjectPBZ-Windows.utoc");
        File.WriteAllBytes(exe, exeBytes); File.WriteAllBytes(toc, tocBytes);
        string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        // Use the actual embedded payload and real service, with disposable game
        // identities. Never bypass version checks against the installed game.
        var manifest = embedded.Manifest;
        manifest.TestedGameExeSha256 = Sha(exeBytes); manifest.TestedGameUtocSha256 = Sha(tocBytes);
        var current = GameGlyphPatchPackage.FileNames.ToDictionary(n => n, embedded.GetFileBytes);
        var package = new GameGlyphPatchPackage(manifest, current);
        var service = new GameGlyphPatchService(package, Path.Combine(root, "data"), _ => false, () => []);
        check(service.ApplyAsync(game).GetAwaiter().GetResult().Success, "actual v0.3 payload installs on disposable game");
        foreach (var pair in current) check(File.ReadAllBytes(Path.Combine(paks, pair.Key)).SequenceEqual(pair.Value), "actual glyph installed integrity " + pair.Key);
        check(service.RestoreAsync(game).GetAwaiter().GetResult().Success && current.Keys.All(n => !File.Exists(Path.Combine(paks, n))),
            "actual glyph restore removes its overlay only");
        check(File.ReadAllBytes(exe).SequenceEqual(exeBytes) && File.ReadAllBytes(toc).SequenceEqual(tocBytes), "install/restore preserves original game files");
        var old = current.ToDictionary(p => p.Key, p => Encoding.UTF8.GetBytes("old fixture " + p.Key));
        manifest.PreviousFileSets.Add(new() { Version = "test-previous", Files = old.Select(p => new GameGlyphPatchFile
        { Name = p.Key, Length = p.Value.Length, Sha256 = Sha(p.Value) }).ToList() });
        service = new(new GameGlyphPatchPackage(manifest, current), Path.Combine(root, "upgrade-data"), _ => false, () => []);
        foreach (var p in old) File.WriteAllBytes(Path.Combine(paks, p.Key), p.Value);
        var before = service.InspectAsync(game).GetAwaiter().GetResult();
        check(before.CanInstall && before.CanRestore && before.InstalledPatchVersion == "test-previous", "known old overlay offers update and restore");
        check(service.ApplyAsync(game).GetAwaiter().GetResult().Success, "old overlay upgrades transactionally to actual v0.3 payload");
        var backups = Directory.GetFiles(Path.Combine(root, "upgrade-data"), "PRO2_PBZ_Glyphs_0_P.ucas", SearchOption.AllDirectories);
        check(backups.Any(p => File.ReadAllBytes(p).SequenceEqual(old["PRO2_PBZ_Glyphs_0_P.ucas"])), "upgrade archives previous overlay before replacing");
        check(service.RestoreAsync(game).GetAwaiter().GetResult().Success, "restore after upgrade");
        File.WriteAllText(toc, "updated game");
        check(!service.ApplyAsync(game).GetAwaiter().GetResult().Success && !File.Exists(Path.Combine(paks, GameGlyphPatchPackage.FileNames[0])),
            "unknown game build cannot receive v0.3 patch");
    }
}
