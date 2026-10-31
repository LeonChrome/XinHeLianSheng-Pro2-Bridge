using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Y700Switch2V60Viiper;

static class Program
{
    private static int checks, cases, skips;
    private static readonly string RunRoot = Path.Combine(Path.GetTempPath(), "PRO2-glyph-service-tests", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
    static async Task Main()
    {
        Directory.CreateDirectory(RunRoot);
        try
        {
            await Case("中文 空格 安装恢复", async f =>
            {
                Equal(f.GameRoot, GameGlyphPatchService.NormalizeGameRoot(f.Exe), "normalize exe");
                Equal(f.GameRoot, GameGlyphPatchService.NormalizeGameRoot(f.Paks), "normalize nested paks");
                Equal(GameGlyphPatchStatus.NotInstalled, (await f.Service.InspectAsync(f.GameRoot)).Status, "baseline");
                var applied = await f.Service.ApplyAsync(f.GameRoot); Check(applied.Success, applied.Message);
                Equal(GameGlyphPatchStatus.Installed, applied.Snapshot.Status, "installed");
                Check(!applied.Snapshot.CanInstall && applied.Snapshot.CanRestore, "installed permissions");
                f.AssertCurrentFiles(); f.AssertOriginals();
                Check((await f.Service.ApplyAsync(f.GameRoot)).Success, "idempotent apply");
                Check((await f.Service.RestoreAsync(f.GameRoot)).Success, "restore");
                Check(FixedFiles(f).All(p => !File.Exists(p)), "removed overlay only");
                f.AssertOriginals(); Check(!File.Exists(Path.Combine(f.Paks, ".pro2-glyph-operation.lock")), "temporary lock removed");
                var records = Directory.GetFiles(f.Data, "operations.log", SearchOption.AllDirectories).SelectMany(File.ReadLines).ToArray();
                Check(records.Length >= 4, "operation log exists");
                foreach (var line in records) using (JsonDocument.Parse(line)) { checks++; }
                var before = Directory.GetFiles(f.Data, "before.json", SearchOption.AllDirectories);
                Check(before.Length == 2, "backup journal for install and restore");
                Check(Directory.GetFiles(f.Data, GameGlyphPatchPackage.FileNames[0], SearchOption.AllDirectories).Length > 0, "restore archives removed patch");
            });
            await Case("已知旧版升级", async f =>
            {
                f.WriteOld(); var s = await f.Service.InspectAsync(f.GameRoot);
                Equal(GameGlyphPatchStatus.Installed, s.Status, "old identified"); Equal("0.1.0", s.InstalledPatchVersion, "old version");
                Check(s.CanInstall && s.CanRestore, "old update and restore offered");
                var result=await f.Service.ApplyAsync(f.GameRoot); Check(result.Success, result.Message); f.AssertCurrentFiles();
                Equal("0.2.0",result.Snapshot.InstalledPatchVersion,"updated version");
                Check(Directory.GetFiles(f.Data, "*.pak", SearchOption.AllDirectories).Any(p => File.ReadAllBytes(p).SequenceEqual(f.Old[GameGlyphPatchPackage.FileNames[0]])), "old data backed up");
                Check((await f.Service.RestoreAsync(f.GameRoot)).Success,"restore upgraded patch"); f.AssertOriginals();
            });
            await Case("手工旧版采用恢复", async f =>
            {
                f.WriteOld(); Check((await f.Service.RestoreAsync(f.GameRoot)).Success,"restore manual known set without state");
                Check(FixedFiles(f).All(p => !File.Exists(p)),"manual set removed"); f.AssertOriginals();
            });
            await Case("未知文件保护", async f =>
            {
                var p=FixedFiles(f)[0];File.WriteAllText(p,"external mod unknown");
                Equal(GameGlyphPatchStatus.Conflict,(await f.Service.InspectAsync(f.GameRoot)).Status,"unknown conflict");
                Check(!(await f.Service.ApplyAsync(f.GameRoot)).Success,"unknown not overwritten");
                Check(!(await f.Service.RestoreAsync(f.GameRoot)).Success,"unknown not removed");
                Equal("external mod unknown",File.ReadAllText(p),"external contents intact"); f.AssertOriginals();
            });
            await Case("缺少文件修复", async f =>
            {
                File.WriteAllBytes(FixedFiles(f)[0],f.Current[GameGlyphPatchPackage.FileNames[0]]);
                Equal(GameGlyphPatchStatus.Partial,(await f.Service.InspectAsync(f.GameRoot)).Status,"partial detected");
                Check((await f.Service.ApplyAsync(f.GameRoot)).Success,"partial repaired"); f.AssertCurrentFiles();
            });
            await Case("已知混合版本修复", async f =>
            {
                f.WriteOld();File.WriteAllBytes(FixedFiles(f)[0],f.Current[GameGlyphPatchPackage.FileNames[0]]);
                Equal(GameGlyphPatchStatus.Partial,(await f.Service.InspectAsync(f.GameRoot)).Status,"known mixture partial");
                Check((await f.Service.ApplyAsync(f.GameRoot)).Success,"known mixture repaired");f.AssertCurrentFiles();
            });
            await Case("游戏更新只许恢复", async f =>
            {
                f.WriteOld();File.AppendAllText(f.Exe,"updated version");
                var before=File.ReadAllBytes(f.Exe);var s=await f.Service.InspectAsync(f.GameRoot);
                Equal(GameGlyphPatchStatus.UnsupportedVersion,s.Status,"unsupported game");Check(!s.CanInstall&&s.CanRestore,"restore available after update");
                Check(!(await f.Service.ApplyAsync(f.GameRoot)).Success,"unsupported not installed");
                Check((await f.Service.RestoreAsync(f.GameRoot)).Success,"unsupported known overlay restored");
                Check(before.SequenceEqual(File.ReadAllBytes(f.Exe)),"new game exe unchanged");
            });
            await Case("游戏运行禁止写入", async f =>
            {
                f.Running=true;var s=await f.Service.InspectAsync(f.GameRoot);
                Equal(GameGlyphPatchStatus.Busy,s.Status,"busy status");Check(!s.CanInstall&&!s.CanRestore,"busy disabled");
                Check(!(await f.Service.ApplyAsync(f.GameRoot)).Success,"busy not installed");
                f.WriteOld();Check(!(await f.Service.RestoreAsync(f.GameRoot)).Success,"busy not restored");f.AssertOldFiles();
            });
            await Case("失败事务回滚", async f =>
            {
                f.WriteOld();
                using(var held=new FileStream(FixedFiles(f)[1],FileMode.Open,FileAccess.Read,FileShare.Read))
                {
                    var result=await f.Service.ApplyAsync(f.GameRoot);Check(!result.Success,"locked second file fails");
                    Check(!result.RequiresElevation,"sharing failure not admin prompt");f.AssertOldFiles();
                    Check(!Directory.EnumerateFiles(f.Paks,"*.tmp").Any(),"staging files cleaned");
                }
                Check((await f.Service.ApplyAsync(f.GameRoot)).Success,"retry after releasing lock");f.AssertCurrentFiles();
            });
            await Case("取消不修改", async f =>
            {
                using var cts=new CancellationTokenSource();cts.Cancel();
                try { await f.Service.ApplyAsync(f.GameRoot,cts.Token);throw new Exception("Cancellation did not propagate"); }
                catch(OperationCanceledException){checks++;}
                Check(FixedFiles(f).All(p=>!File.Exists(p)),"cancelled overlay absent");f.AssertOriginals();
            });
            await Case("跨账户目标锁", async f =>
            {
                using(var held=new FileStream(Path.Combine(f.Paks,".pro2-glyph-operation.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None,1,FileOptions.DeleteOnClose))
                {
                    var other=new GameGlyphPatchService(f.Package,Path.Combine(f.Data,"other-user"),_=>false,()=>Array.Empty<string>());
                    Check(!(await other.ApplyAsync(f.GameRoot)).Success,"different state root still serialized");
                    Check(FixedFiles(f).All(p=>!File.Exists(p)),"no concurrent write");
                }
                Check((await f.Service.ApplyAsync(f.GameRoot)).Success,"lock released permits install");
            });
            await Case("同名目录保护", async f =>
            {
                Directory.CreateDirectory(FixedFiles(f)[0]);
                Check(!(await f.Service.ApplyAsync(f.GameRoot)).Success,"directory never replaced");
                Check(Directory.Exists(FixedFiles(f)[0]),"directory remains");
            });
            await Case("Steam现代和旧库格式", async f =>
            {
                string steam=Path.Combine(f.Base,"Steam Client"),lib=Path.Combine(f.Base,"中文 游戏库"),oldlib=Path.Combine(f.Base,"Old Library");
                Directory.CreateDirectory(Path.Combine(steam,"steamapps"));
                string ModernGame(string library,string folder){var game=Path.Combine(library,"steamapps","common",folder);CreateGame(game);return game;}
                string game1=ModernGame(lib,"PBZ renamed folder"),game2=ModernGame(oldlib,"PBZ");
                static string Escape(string value)=>value.Replace("\\","\\\\");
                File.WriteAllText(Path.Combine(steam,"steamapps","libraryfolders.vdf"),$"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{Escape(steam)}\" }} \"1\" {{ \"path\" \"{Escape(lib)}\" }} \"2\" \"{Escape(oldlib)}\" }}");
                File.WriteAllText(Path.Combine(lib,"steamapps","appmanifest_4115450.acf"),"\"AppState\" { \"appid\" \"4115450\" \"name\" \"Phantom Blade Zero\" \"installdir\" \"PBZ renamed folder\" }");
                File.WriteAllText(Path.Combine(oldlib,"steamapps","appmanifest_4115450.acf"),"// old layout\n\"AppState\" { \"appid\" \"4115450\" \"installdir\" \"PBZ\" }");
                var service=new GameGlyphPatchService(f.Package,f.Data,_=>false,()=>new[]{steam});var discovered=service.DiscoverSteamInstallations();
                Equal(2,discovered.Count,"multiple libraries");Check(discovered.Any(x=>x.GameRoot==game1&&x.SteamAppId=="4115450"),"manifest nonstandard folder");Check(discovered.Any(x=>x.GameRoot==game2),"old library format");
                File.WriteAllText(Path.Combine(steam,"steamapps","appmanifest_4115450.acf"),"\"AppState\" { \"appid\" \"4115450\" \"installdir\" \"..\\\\escaped\" }");
                Equal(2,service.DiscoverSteamInstallations().Count,"traversal manifest ignored");
                await Task.CompletedTask;
            });
            await Case("Steam库junction", async f =>
            {
                var alias=Path.Combine(f.Base,"Library Junction");
                using(var p=new Process{StartInfo=new ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}})
                {
                    p.StartInfo.ArgumentList.Add("/d");p.StartInfo.ArgumentList.Add("/c");p.StartInfo.ArgumentList.Add("mklink");p.StartInfo.ArgumentList.Add("/J");p.StartInfo.ArgumentList.Add(alias);p.StartInfo.ArgumentList.Add(f.GameRoot);p.Start();await p.WaitForExitAsync();
                    if(p.ExitCode!=0){Console.WriteLine("SKIP junction creation: "+await p.StandardError.ReadToEndAsync());skips++;return;}
                }
                var normalized=GameGlyphPatchService.NormalizeGameRoot(alias);Check(normalized!=null,"junction accepted");
                var result=await f.Service.ApplyAsync(alias);Check(result.Success,result.Message);f.AssertCurrentFiles();
                Check((await f.Service.RestoreAsync(alias)).Success,"junction restore");f.AssertOriginals();
            });
            await Case("补丁文件链接拒绝", async f =>
            {
                var external=Path.Combine(f.Base,"outside.pak");File.WriteAllText(external,"external");
                try{File.CreateSymbolicLink(FixedFiles(f)[0],external);}catch(Exception ex) when(ex is UnauthorizedAccessException or IOException){Console.WriteLine("SKIP symbolic link creation unavailable");skips++;return;}
                Check(!(await f.Service.ApplyAsync(f.GameRoot)).Success,"link not overwritten");Check(!(await f.Service.RestoreAsync(f.GameRoot)).Success,"link not removed");Equal("external",File.ReadAllText(external),"link target intact");
            });
            await Case("损坏内置包拒绝", async f =>
            {
                var damaged=f.Current.ToDictionary(x=>x.Key,x=>x.Value.ToArray());damaged[GameGlyphPatchPackage.FileNames[0]][0]^=1;
                try { _=new GameGlyphPatchPackage(f.Package.Manifest,damaged);throw new Exception("Corrupted package accepted"); }
                catch(InvalidDataException){checks++;}
                var manifest=f.Package.Manifest;manifest.Version="";
                try { _=new GameGlyphPatchPackage(manifest,f.Current);throw new Exception("Empty version accepted"); }
                catch(InvalidDataException){checks++;}
                Check(FixedFiles(f).All(p=>!File.Exists(p)),"invalid package did not write");
                await Task.CompletedTask;
            });
            Console.WriteLine($"PASS cases={cases} checks={checks} skips={skips} artifacts={RunRoot}");
            File.WriteAllText(Path.Combine(RunRoot,"result.json"),JsonSerializer.Serialize(new{Success=true,Cases=cases,Checks=checks,Skips=skips,Artifacts=RunRoot},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception ex){Console.Error.WriteLine(ex);Environment.ExitCode=1;}
    }
    static async Task Case(string name,Func<Fixture,Task> action){using var f=new Fixture(Path.Combine(RunRoot,(++cases).ToString("D2")+" "+name));await action(f);Console.WriteLine("PASS "+name);}
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static void Equal<T>(T expected,T actual,string message)=>Check(EqualityComparer<T>.Default.Equals(expected,actual),$"{message}: expected={expected} actual={actual}");
    static string[] FixedFiles(Fixture f)=>GameGlyphPatchPackage.FileNames.Select(n=>Path.Combine(f.Paks,n)).ToArray();
    static string Sha(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    static readonly byte[] ExeBytes=Encoding.UTF8.GetBytes("fixture PBZ executable original"),UtocBytes=Encoding.UTF8.GetBytes("fixture PBZ TOC original");
    static void CreateGame(string root){Directory.CreateDirectory(Path.Combine(root,"platform","steam"));Directory.CreateDirectory(Path.Combine(root,"ProjectPBZ","Content","Paks"));File.WriteAllBytes(Path.Combine(root,"platform","steam","PBZ.exe"),ExeBytes);File.WriteAllBytes(Path.Combine(root,"ProjectPBZ","Content","Paks","ProjectPBZ-Windows.utoc"),UtocBytes);}
    sealed class Fixture:IDisposable
    {
        public string Base,GameRoot,Paks,Exe,Data;public bool Running;
        public Dictionary<string,byte[]> Current,Old;public GameGlyphPatchPackage Package;public GameGlyphPatchService Service;
        public Fixture(string root)
        {
            Base=root;GameRoot=Path.Combine(root,"游戏 Game 安装");Paks=Path.Combine(GameRoot,"ProjectPBZ","Content","Paks");Exe=Path.Combine(GameRoot,"platform","steam","PBZ.exe");Data=Path.Combine(root,"user-data");CreateGame(GameRoot);
            Current=GameGlyphPatchPackage.FileNames.ToDictionary(n=>n,n=>Encoding.UTF8.GetBytes("current 0.2 "+n));Old=GameGlyphPatchPackage.FileNames.ToDictionary(n=>n,n=>Encoding.UTF8.GetBytes("old 0.1 "+n));
            List<GameGlyphPatchFile> Set(Dictionary<string,byte[]> bytes)=>bytes.Select(x=>new GameGlyphPatchFile{Name=x.Key,Length=x.Value.Length,Sha256=Sha(x.Value)}).ToList();
            Package=new(new GameGlyphPatchManifest{Version="0.2.0",TestedGameExeSha256=Sha(ExeBytes),TestedGameUtocSha256=Sha(UtocBytes),Files=Set(Current),PreviousFileSets=new(){new(){Version="0.1.0",Files=Set(Old)}}},Current);
            Service=new(Package,Data,_=>Running,()=>Array.Empty<string>());
        }
        public void WriteOld(){foreach(var pair in Old)File.WriteAllBytes(Path.Combine(Paks,pair.Key),pair.Value);}
        public void AssertCurrentFiles(){foreach(var pair in Current)Check(File.ReadAllBytes(Path.Combine(Paks,pair.Key)).SequenceEqual(pair.Value),"current "+pair.Key);}
        public void AssertOldFiles(){foreach(var pair in Old)Check(File.ReadAllBytes(Path.Combine(Paks,pair.Key)).SequenceEqual(pair.Value),"rollback old "+pair.Key);}
        public void AssertOriginals(){Check(File.ReadAllBytes(Exe).SequenceEqual(ExeBytes),"original exe untouched");Check(File.ReadAllBytes(Path.Combine(Paks,"ProjectPBZ-Windows.utoc")).SequenceEqual(UtocBytes),"original utoc untouched");}
        public void Dispose(){} // Keep test journals and snapshots; no recursive cleanup of junctions.
    }
}
