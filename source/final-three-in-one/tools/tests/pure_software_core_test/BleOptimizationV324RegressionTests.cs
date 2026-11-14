using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Y700Switch2V60Viiper;

internal static class BleOptimizationV324RegressionTests
{
    public static async Task RunAsync(Action<bool,string> check)
    {
        var native=new BleAdaptiveMeasurement(new(10,100,100,5,true,true,180,48),MaximumGapMs:240);
        var retained=new BleAdaptiveMeasurement(new(10,701,701,5,true,true,23,6),MaximumGapMs:31);
        var final=new BleAdaptiveMeasurement(new(10,679,679,5,true,false,22.7,6),MaximumGapMs:31);
        check(BleAdaptivePresetPolicy.VerifyFinal(native,retained,final).Code=="final_playable_preserved"&&
            !final.Sample.RequestedSuccessfully,"pure FINAL measurement preserves compatibility without fabricating API success");
        check(!BleAdaptivePresetPolicy.EvaluateCandidate(native,native,final).Retain&&
            BleAdaptivePresetPolicy.EvaluateContinuation(native,native,final).Retain,
            "new candidate still requires request success while a continuation is independently measured");
        foreach(var invalid in new[]{final with{CompletedAgeSeconds=16},final with{Sample=final.Sample with{ParsedCount=0}},
            final with{Sample=final.Sample with{LastInputAgeMs=500}},final with{MaximumGapMs=150},
            final with{Sample=final.Sample with{Connected=false}}})
            check(!BleAdaptivePresetPolicy.VerifyFinal(native,retained,invalid).Retain,"measurement-only verification does not relax safety evidence");

        const string pendingZh="设备正在等待系统重新启动，以完成上一个操作。\r\n该操作系统产品不支持此命令。";
        const string pendingEn="The device is pending a system reboot to complete a previous operation.";
        foreach(string message in new[]{pendingZh,pendingEn,"A system restart is required to complete a previous operation."})
        {
            check(BluetoothRebootEvidence.RequiresReboot(50,null,message),"exit 50 plus explicit pending message is recognized");
            var result=new BluetoothRestartResult("op","pnp","id",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,
                true,50,true,true,true,"",message);
            check(result.RebootRequired&&!result.Verified&&result.Telemetry.Contains("reboot_required=True"),
                "cached result and log agree about a pending reboot even when exit is not 3010");
        }
        foreach(string message in new[]{"Device restart failed. The request is not supported.","The request is not supported.",
            "Cannot restart the device.","reboot", "restart", "不支持重启设备","设备不在等待系统重新启动",
            "The device is not pending a system reboot to complete a previous operation."})
            check(!BluetoothRebootEvidence.RequiresReboot(50,50,message),"generic restart words cannot invent a reboot requirement");
        check(BluetoothRebootEvidence.RequiresReboot(0,50,pendingZh)&&BluetoothRebootEvidence.RequiresReboot(3010,null,"")&&
            BluetoothRebootEvidence.RequiresReboot(0,3010,""),"disable and enable exit evidence both survive");
        check(BluetoothRebootEvidence.DeviceStatusRequiresReboot(0x100,0)&&BluetoothRebootEvidence.DeviceStatusRequiresReboot(8,14)&&
            !BluetoothRebootEvidence.DeviceStatusRequiresReboot(0x80,0),"DN_NEED_RESTART and Code14 are distinct from hardware enumeration");
        var preflight=new BluetoothRestartResult("op","pnp","id",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,
            false,null,true,true,true,BluetoothRebootEvidence.Guidance,"",DeviceRebootPending:true);
        check(preflight.RebootRequired&&!preflight.Verified,"read-only device-status preflight can stop a hot reset before mutation");
        var truncated=preflight with{DeviceRebootPending=false,ToolRebootPending=true,RestartAttempted=true,NativeExitCode=50};
        var roundtrip=JsonSerializer.Deserialize<BluetoothRestartResult>(JsonSerializer.Serialize(truncated))!;
        check(roundtrip.RebootRequired&&!roundtrip.Verified,"bounded native output cannot discard previously captured pending-reboot evidence");

        var lines=new List<string>();
        var logger=new BleOrderedDiagnosticLog(lines.Add);
        IProgress<string> progress=new DirectProgress<string>(logger.Report);
        progress.Report("inline");check(lines.Count==1,"CLI progress is written before Report returns");
        await Task.WhenAll(Enumerable.Range(0,8).Select(worker=>Task.Run(()=>
        {for(int i=0;i<200;i++)progress.Report("worker="+worker+" entry="+i);})));
        check(lines.Count==1601,"concurrent CLI logging loses no entries");
        long previousQpc=0;
        for(int i=0;i<lines.Count;i++)
        {
            int start=lines[i].IndexOf(" qpc=",StringComparison.Ordinal)+5,end=lines[i].IndexOf(']',start);
            long qpc=long.Parse(lines[i][start..end]);
            check(lines[i].Contains("seq="+(i+1)+" qpc=")&&qpc>=previousQpc,"write order, sequence and monotonic acceptance timestamp agree");
            previousQpc=qpc;
        }

        byte[] Packet(uint motion,uint counter)
        {
            byte[] payload=new byte[63];BinaryPrimitives.WriteUInt32LittleEndian(payload,counter);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(42,4),motion);return payload;
        }
        long T(double ms)=>(long)Math.Round(ms*Stopwatch.Frequency/1000.0);
        var uniform=new Fd2DeviceTimingWindow();
        for(uint i=0;i<101;i++)uniform.Record(Packet(i*7500,(uint)(i*7.5)),T(i*7.5));
        var summary=uniform.Snapshot();
        check(summary.ValidPairs==100&&summary.DeviceDtP50Ms==7.5&&summary.DeviceDtP95Ms==7.5&&
            summary.HostMinusDeviceJitterMaxMs<.001&&summary.CounterSteps["7"]==50&&summary.CounterSteps["8"]==50,
            "device motion and raw counter distributions are window-local with explicit scale");
        var bimodal=new Fd2DeviceTimingWindow();double host=0;uint motionTime=0;
        for(uint i=0;i<101;i++)
        {
            bimodal.Record(Packet(motionTime,i),T(host));
            double delta=i%2==0?7.5:22.5;host+=delta;motionTime+=(uint)(delta*1000);
        }
        var bimodalResult=bimodal.Snapshot();
        check(bimodalResult.DeviceDtP50Ms==7.5&&bimodalResult.DeviceDtP95Ms==22.5&&
            bimodalResult.ToLogFields().Contains("device_timing_attribution=not_determined"),
            "matching host/device bimodality is evidence about observed reports, not proof of firmware or packet loss");
        var deliveryVariation=new Fd2DeviceTimingWindow();
        deliveryVariation.Record(Packet(0,0),T(0));deliveryVariation.Record(Packet(7500,7),T(22.5));
        check(Math.Abs(deliveryVariation.Snapshot().HostMinusDeviceJitterMaxMs!.Value-15)<.001,
            "host/device delta difference is measured without assigning its cause to air or Windows");
        var wrap=new Fd2DeviceTimingWindow();
        wrap.Record(Packet(uint.MaxValue-3000,uint.MaxValue-3),T(0));wrap.Record(Packet(4499,4),T(7.5));
        check(wrap.Snapshot().DeviceDtP50Ms==7.5&&wrap.Snapshot().CounterSteps["8"]==1,"uint wrap is a small forward delta, not a clock reset");
        var reordered=new Fd2DeviceTimingWindow();
        reordered.Record(Packet(7500,7),T(7.5));reordered.Record(Packet(0,0),T(0));reordered.Record(Packet(15000,15),T(15));
        check(reordered.Snapshot().ReorderedPackets==2&&reordered.Snapshot().ValidPairs==2,"concurrent callback ordering is normalized before clock deltas");
        var reset=new Fd2DeviceTimingWindow();
        reset.Record(Packet(100000,0),T(0));reset.Record(Packet(100000,1),T(7.5));reset.Record(Packet(5000,2),T(15));
        reset.Record(new byte[8],T(22.5));
        var resetResult=reset.Snapshot();
        check(resetResult.RepeatedMotionTimestamps==1&&resetResult.MotionClockDiscontinuities==1&&resetResult.MalformedPackets==1&&
            resetResult.DeviceDtP50Ms==null&&resetResult.HostMinusDeviceJitterMaxMs==null,"missing/duplicate/reset clocks never become misleading zero or huge elapsed times");
        var bounded=new Fd2DeviceTimingWindow();
        for(uint i=0;i<Fd2DeviceTimingWindow.Capacity+50;i++)bounded.Record(Packet(i*7500,i),T(i*7.5));
        check(bounded.Snapshot().StoredPackets==Fd2DeviceTimingWindow.Capacity&&bounded.Snapshot().SamplesTruncated,
            "per-notify memory remains bounded and truncation is disclosed");
        check(JsonSerializer.Deserialize<Fd2DeviceTimingSummary>(JsonSerializer.Serialize(summary))!.DeviceDtP95Ms==7.5,
            "per-window timing evidence survives diagnostic JSON export");
    }
}
