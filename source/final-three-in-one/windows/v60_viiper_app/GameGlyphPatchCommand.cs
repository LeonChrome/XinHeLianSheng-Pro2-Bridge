using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed record GameGlyphHelperResult(string OperationId, string Action, string GameRoot, bool Success, string Message);

public static class GameGlyphPatchCommand
{
    public const string Command = "--game-glyph-patch";
    public static bool IsCommand(string[] args) => args.Contains(Command, StringComparer.OrdinalIgnoreCase);
    private static string ResultPath(string operation)
    {
        if (!Guid.TryParseExact(operation, "N", out _)) throw new ArgumentException("无效的操作编号。");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PRO2ControllerAssistant", "GameGlyphResults", operation + ".json");
    }

    public static async Task<GameGlyphHelperResult> LaunchElevatedAsync(string path, bool restore)
    {
        string operation = Guid.NewGuid().ToString("N"), action = restore ? "restore" : "apply";
        string root = GameGlyphPatchService.NormalizeGameRoot(path) ?? throw new IOException("游戏目录无效，请重新检测。");
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("无法定位当前程序。"))
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            foreach (string argument in new[] { Command, action, "--game-root", root, "--operation-id", operation })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("管理员辅助进程未启动。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await process.WaitForExitAsync(timeout.Token);
            string resultFile = ResultPath(operation);
            if (File.Exists(resultFile) && new FileInfo(resultFile).Length < 65536)
            {
                var result = JsonSerializer.Deserialize<GameGlyphHelperResult>(await File.ReadAllTextAsync(resultFile));
                if (result != null && result.OperationId == operation && result.Action == action &&
                    result.GameRoot.Equals(root, StringComparison.OrdinalIgnoreCase)) return result;
            }
            return new(operation, action, root, false, "未收到完整结果，请点击检测确认文件状态。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { return new(operation, action, root, false, "已取消管理员授权，未继续操作。"); }
        catch (OperationCanceledException)
        { return new(operation, action, root, false, "操作结果暂未返回，请稍后点击检测确认。不会自动重试。"); }
    }

    public static async Task<int> ExecuteAsync(string[] args)
    {
        if (args.Length != 6 || args[0] != Command || args[1] is not ("apply" or "restore") ||
            args[2] != "--game-root" || args[4] != "--operation-id") return 7;
        string operation = args[5], action = args[1], root;
        string file;
        try { file = ResultPath(operation); root = GameGlyphPatchService.NormalizeGameRoot(args[3]) ?? throw new IOException("游戏目录无效。"); }
        catch { return 7; }
        try
        {
            string? parent = Path.GetDirectoryName(file);
            for (string? path = parent; path != null; path = Path.GetDirectoryName(path))
                if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("结果目录含重解析点，已停止操作。");
            Directory.CreateDirectory(parent!);
            if (File.Exists(file)) return 7;
            GameGlyphHelperResult result;
            try
            {
                var service = new GameGlyphPatchService();
                var operationResult = action == "restore" ? await service.RestoreAsync(root) : await service.ApplyAsync(root);
                result = new(operation, action, root, operationResult.Success, operationResult.Message);
            }
            catch (Exception ex) { result = new(operation, action, root, false, ex.Message); }
            await using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await JsonSerializer.SerializeAsync(stream, result);
            return result.Success ? 0 : 1;
        }
        catch { return 5; }
    }
}
