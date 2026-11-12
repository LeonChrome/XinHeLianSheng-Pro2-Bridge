using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using Y700Switch2V60Viiper;

internal static class V29RegressionTests
{
    public static void Run(Action<bool,string> check, string output)
    {
        var parser = new Pro2HidReportParser(false);
        parser.SetStickCalibration(new Pro2StickCalibrationProfile { CenterCalibrated = true });
        byte[] Payload(int left, int right)
        {
            var bytes = new byte[63];
            Pack(bytes,10,left,2048); Pack(bytes,13,right,2048);
            return bytes;
        }
        static void Pack(byte[] bytes,int offset,int x,int y)
        { bytes[offset]=(byte)x; bytes[offset+1]=(byte)((x>>8)|((y&15)<<4)); bytes[offset+2]=(byte)(y>>4); }
        var tiny = Payload(2080,2080);
        check(parser.TryParseFd2Payload(tiny,out var baseline,out _) && baseline.Lx==2048 && baseline.Rx==2048,
            "legacy default four-percent deadzone preserved");
        parser.SetStickDeadzones(new(0,10));
        var rawCopy=tiny.ToArray();
        check(parser.TryParseFd2Payload(tiny,out var adjusted,out _) && adjusted.Lx>2048 && adjusted.Rx==2048,
            "zero left deadzone restores small movement independently from right deadzone");
        check(tiny.SequenceEqual(rawCopy) && adjusted.Buttons==baseline.Buttons && adjusted.GyroX==baseline.GyroX,
            "deadzone adjustment does not alter raw FD2, buttons or gyro");
        var uncalibrated = new Pro2HidReportParser(false);
        uncalibrated.SetStickDeadzones(new(0,0));
        for(int i=0;i<200;i++)
        {
            uncalibrated.TryParseFd2Payload(tiny,out var steady,out _);
            check(steady.Lx>2048 && steady.Rx>2048,"reduced deadzone does not auto-recenter away sustained small input");
        }
        foreach (double percentage in new[]{0d,1d,4d,10d,20d})
        {
            parser.SetStickDeadzones(new(percentage,percentage));
            int previous=-1;
            for(int x=448;x<=3648;x+=2)
            {
                parser.TryParseFd2Payload(Payload(x,x),out var sample,out _);
                check(sample.Lx>=previous && sample.Lx==sample.Rx,"stick deadzone monotonic and symmetric across sticks");
                previous=sample.Lx;
            }
            parser.TryParseFd2Payload(Payload(2048,2048),out var center,out _);
            parser.TryParseFd2Payload(Payload(448,3648),out var endpoints,out _);
            check(center.Lx==2048 && center.Rx==2048 && endpoints.Lx==0 && endpoints.Rx==4095,"deadzone preserves center and full range " + percentage);
        }
        check(new StickDeadzoneSettings(double.NaN,99).Normalize()==new StickDeadzoneSettings(4,20),"invalid settings safely normalized");
        var legacy=JsonSerializer.Deserialize<V60UserSettings>("{}")!;
        check(legacy.StickDeadzones==new StickDeadzoneSettings(4,4),"older users retain existing default");
        foreach(string name in new[]{"Pro 2 Controller","PRO_2","NS2Pro","Nintendo Switch 2 Pro Controller"})
            check(Pro2AdvertisementIdentity.Score(name,false,[])>0,"additional Pro2 name accepted " + name);
        check(Pro2AdvertisementIdentity.Score("",false,[Pro2AdvertisementIdentity.Fd2Service])>=80,"service identity supports nameless advertisement");
        check(Pro2AdvertisementIdentity.Score("headphones",false,[Guid.NewGuid()])==0,"unrelated advertisements not auto-connected");
        var repeat=new RearButtonEditor("C 键",true,new());
        repeat.SouthChoice.Selected=true; repeat.Enabled=true; repeat.RepeatPresetCommand.Execute("fast");
        check(repeat.ActionSummary.Contains("×") && repeat.TryBuild(out var binding,out _) && binding.PressMs==40 && binding.GapMs==40 && binding.RepeatCount==0,
            "visual repeat preset retains hold-to-repeat safety semantics");
        repeat.Action="顺序按键"; repeat.AddStepCommand.Execute(null); repeat.AddStepCommand.Execute(null);
        repeat.Steps[0].RemoveCommand.Execute(null);
        check(repeat.Steps.Count==1 && repeat.Steps[0].StepNumber==2 && repeat.SequencePreview.Contains("→"),"sequence visualization renumbers after removal");
        repeat.ClearSelectionCommand.Execute(null);
        check(!repeat.TryBuild(out _,out _),"clearing an enabled mapping requires selecting a valid output");
        TestUpdatedGame(check,output);
        // The old probe waited for exit without draining stdout: this valid
        // command fills the Windows pipe and reproduces its false timeout.
        string shell=Environment.GetEnvironmentVariable("ComSpec")!;
        var probe=UsbipRuntimeLocator.ProbeAsync(new(shell,Path.GetDirectoryName(shell)!),default,
            "/d /c for /L %i in (1,1,2000) do @echo 012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789").GetAwaiter().GetResult();
        check(probe.Ready && probe.Detail.Contains("output truncated"),"USBIP detection drains large output without false driver timeout");
    }

    private static void TestUpdatedGame(Action<bool,string> check,string output)
    {
        var package=GameGlyphPatchPackage.LoadEmbedded();
        string root=Path.Combine(output,"glyph updated build 中文"), paks=Path.Combine(root,"ProjectPBZ","Content","Paks");
        Directory.CreateDirectory(paks); Directory.CreateDirectory(Path.Combine(root,"platform","steam"));
        File.WriteAllText(Path.Combine(root,"platform","steam","PBZ.exe"),"new game executable");
        byte[] toc=package.GetFileBytes("PRO2_PBZ_Glyphs_0_P.utoc");
        // Separate game container identity with the same actual target assets.
        toc[56]^=0x27;
        string target=Path.Combine(paks,"ProjectPBZ-Windows.utoc");
        File.WriteAllBytes(target,toc); File.WriteAllBytes(Path.ChangeExtension(target,".ucas"),[1]);
        var service=new GameGlyphPatchService(package,Path.Combine(output,"glyph-update-state"),_=>false,()=>[]);
        var before=service.InspectAsync(root).GetAwaiter().GetResult();
        check(before.CanInstall && before.CompatibilityDetail.Contains("18/18"),"updated EXE/container accepted by matching actual glyph resource IDs");
        check(service.ApplyAsync(root).GetAwaiter().GetResult().Success,"updated game installs additive patch");
        // A formerly installed overlay cannot certify itself after a target disappears.
        int header=(int)BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(20));
        toc[header]^=1; File.WriteAllBytes(target,toc);
        var incompatible=service.InspectAsync(root).GetAwaiter().GetResult();
        check(!incompatible.CanInstall && incompatible.CanRestore,"missing original asset rejects reapply but permits restoration");
        check(service.RestoreAsync(root).GetAwaiter().GetResult().Success,"restoration works after incompatible game update");
        check(File.ReadAllBytes(target).SequenceEqual(toc),"restore leaves updated original game archive unchanged");
        toc[16]=99; File.WriteAllBytes(target,toc);
        check(!service.InspectAsync(root).GetAwaiter().GetResult().CanInstall,"unknown engine container format not blindly patched");
    }
}
