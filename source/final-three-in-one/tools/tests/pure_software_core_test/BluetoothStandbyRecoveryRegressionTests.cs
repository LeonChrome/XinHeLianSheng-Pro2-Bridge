using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Y700Switch2V60Viiper;

internal static class BluetoothStandbyRecoveryRegressionTests
{
    public static async Task RunAsync(Action<bool,string> check)
    {
        foreach(var target in new[]{BluetoothRestartRegressionTests.Target("0BDA","0852"),BluetoothRestartRegressionTests.Target("8087","0033"),
            BluetoothRestartRegressionTests.Target("0E8D","0616"),BluetoothRestartRegressionTests.Target("10D7","B012")})
        {
            for(int mask=1;mask<=15;mask++)
            {
                var launch=new BluetoothRecoveryLaunch(Guid.NewGuid().ToString("N"),target.PnpId,target.InterfaceId,mask);
                check(BluetoothRecoveryLaunch.Parse(launch.Arguments)==launch,"recovery preserves exact radio identity and all four-slot masks without a vendor/hash allowlist");
            }
            string[] args=BluetoothAdapterRestart.ReinitializeArguments(target,Guid.NewGuid().ToString("N"),"1234:123456789");
            check(args.Length==11&&args[8]=="reinitialize"&&args[10]=="1234:123456789"&&!args.Contains("/force")&&!args.Contains("/reboot"),
                "reinitialization requires exact target and parent process identity; no forced driver change or reboot");
            string restore=BluetoothAdapterRestart.RestoreCommand(target);
            check(restore.Contains("/enable-device \""+target.PnpId+"\"")&&restore.Contains("DisableDelayedExpansion")&&!restore.Contains("/remove-device"),
                "recovery backup includes an exact-device enable command");
        }
        var valid=BluetoothRestartRegressionTests.Target("8087","0033");
        var ticket=new BluetoothRecoveryLaunch(Guid.NewGuid().ToString("N"),valid.PnpId,valid.InterfaceId,1);
        foreach(var args in new[]{Array.Empty<string>(),ticket.Arguments.Concat(new[]{"extra"}).ToArray(),
            new[]{"prefix"}.Concat(ticket.Arguments).ToArray(),(ticket with{SlotMask=0}).Arguments,(ticket with{SlotMask=16}).Arguments})
            check(BluetoothRecoveryLaunch.Parse(args)==null,"malformed or ambiguous recovery launch is rejected");
        foreach(var invalid in new[]{ticket with{Operation="../../bad"},ticket with{PnpId="*"},
            ticket with{InterfaceId=valid.InterfaceId.Replace("92383b0e","00000000")}})
            check(BluetoothRecoveryLaunch.Parse(invalid.Arguments)==null,"invalid nonce or foreign adapter cannot authorize recovery");
        foreach(string parent in new[]{"", "0:123", "-1:12", "1234:0", "1234:-1", "1234:9999999999999999999", "12:123:4", "12:12\n/reboot"})
            check(!BluetoothRecoveryLaunch.IsParentToken(parent),"invalid parent token cannot start recovery helper");
        check(await BluetoothAdapterRestart.ExecuteAsync([BluetoothAdapterRestart.Command,"--operation-id",ticket.Operation,"--adapter-pnp",valid.PnpId,
            "--adapter-interface",valid.InterfaceId,"--mode","reinitialize","--wait-parent","invalid"])==7,"malformed deep-recovery helper exits before administrator or device calls");

        var order=new List<string>();
        async Task<BluetoothReinitializationResult> Sequence(int down,bool confirmed,bool failDown=false,bool failSettle=false,bool failEnable=false)
        {
            order.Clear();
            return await BluetoothReinitialization.RunAsync(()=>{order.Add("disable");if(failDown)throw new IOException("disable failed");return Task.FromResult(down);},
                ()=>{order.Add("confirm");return Task.FromResult(confirmed);},
                ()=>{order.Add("enable");if(failEnable)throw new IOException("enable failed");return Task.FromResult(0);},
                ()=>{order.Add("settle");if(failSettle)throw new IOException("settle failed");return Task.CompletedTask;},default);
        }
        var result=await Sequence(0,true);
        check(result.SequenceSucceeded&&order.SequenceEqual(new[]{"disable","confirm","settle","enable"}),"disable is verified and settled before enable");
        result=await Sequence(0,true,failDown:true);
        check(!result.SequenceSucceeded&&result.EnableAttempted&&order.SequenceEqual(new[]{"disable","enable"}),"failed disable still executes independent enable in finally");
        result=await Sequence(0,true,failSettle:true);
        check(!result.SequenceSucceeded&&result.EnableExit==0&&result.Error.Contains("settle failed"),"successful enable cannot hide an earlier failure");
        result=await Sequence(0,true,failEnable:true);
        check(!result.SequenceSucceeded&&result.EnableExit==null&&result.Error.Contains("enable failed"),"failed enable cannot be reported as recovered");
        result=await Sequence(3010,false);
        check(!result.SequenceSucceeded&&result.NeedsReboot&&result.EnableAttempted&&!order.Contains("settle"),"3010 remains pending reboot despite a successful enable");
        result=await Sequence(0,false);
        check(!result.SequenceSucceeded&&result.EnableAttempted&&!order.Contains("settle"),"device that never disabled is not falsely accepted as reinitialized");
        using(var cancel=new CancellationTokenSource())
        {
            int up=0;
            result=await BluetoothReinitialization.RunAsync(()=>{cancel.Cancel();return Task.FromResult(0);},()=>Task.FromResult(true),
                ()=>{up++;return Task.FromResult(0);},()=>Task.CompletedTask,cancel.Token);
            check(up==1&&result.EnableAttempted&&!result.SequenceSucceeded,"cancellation after disable never cancels the recovery enable");
            up=0;
            result=await BluetoothReinitialization.RunAsync(()=>Task.FromResult(0),()=>Task.FromResult(true),
                ()=>{up++;return Task.FromResult(0);},()=>Task.CompletedTask,cancel.Token);
            check(up==0&&!result.EnableAttempted&&!result.SequenceSucceeded,"pre-cancelled request makes no radio changes");
        }
        var stamp=DateTimeOffset.UtcNow;
        var evidence=new BluetoothRestartResult(ticket.Operation,valid.PnpId,valid.InterfaceId,stamp,stamp,true,0,true,true,true,"synthetic","",
            "reinitialize",0,true,true);
        check(evidence.Verified&&!evidence.RebootRequired,"deep recovery requires verified down/up sequence plus driver and parameter checks");
        foreach(var bad in new[]{evidence with{DisableExitCode=3010},evidence with{DisabledConfirmed=false},evidence with{EnableAttempted=false},
            evidence with{NativeExitCode=5},evidence with{PresentStarted=false},evidence with{DriverUnchanged=false},evidence with{RecoveryError="incomplete"},evidence with{Mode="unknown"}})
            check(!bad.Verified,"partial, reboot-pending, failed or unknown recovery cannot be accepted");
        check((evidence with{DisableExitCode=3010}).RebootRequired,"disable requiring reboot is preserved separately from enable exit 0");

        long clock=100;
        var gate=new BleHostResumeGate(()=>clock);
        check(gate.Remaining==TimeSpan.Zero,"normal gameplay has no host-resume wait");
        gate.Suspend();clock+=60*Stopwatch.Frequency;
        check(gate.Remaining==Timeout.InfiniteTimeSpan,"no new connection is admitted while the host is suspended");
        using(var cancel=new CancellationTokenSource())
        {
            var waiting=gate.WaitAsync(cancel.Token);check(!waiting.IsCompleted,"suspend barrier does not complete early");cancel.Cancel();
            try{await waiting;check(false,"stop must cancel a suspended wait");}catch(OperationCanceledException){}
        }
        gate.Resume();
        check(gate.Remaining==TimeSpan.FromSeconds(3),"system resume gives Windows three seconds before new connection attempts");
        clock+=2*Stopwatch.Frequency;check(gate.Remaining==TimeSpan.FromSeconds(1),"settle interval uses monotonic time");
        clock+=Stopwatch.Frequency;
        await gate.WaitAsync(default);check(gate.Remaining==TimeSpan.Zero,"connections resume after the finite settle interval");

        var recovery=new Pro2BleConnectionRecovery(()=>clock);
        var pending=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var released=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        try{await recovery.AwaitAsync(pending.Task,TimeSpan.FromMilliseconds(5),n=>released.TrySetResult(n),default,"service_discovery");check(false,"test operation must time out");}
        catch(TimeoutException){}
        check(recovery.IsDraining&&!recovery.IsStalled&&recovery.PendingDiagnostic.Contains("service_discovery"),"timed-out native operation stays owned and its stage is recorded");
        clock+=31*Stopwatch.Frequency;
        check(recovery.IsStalled&&recovery.PendingAge.TotalSeconds==31&&!released.Task.IsCompleted&&recovery.PendingDiagnostic.Contains("native_request_cancelled=false"),
            "long pending request is diagnosed without claiming Windows cancelled it or stacking more GATT");
        pending.SetResult(7);check(await released.Task.WaitAsync(TimeSpan.FromSeconds(2))==7,"late result is disposed by its original owner");
        using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2)))while(recovery.IsDraining)await Task.Delay(1,timeout.Token);
        check(!recovery.IsStalled&&recovery.PendingAge==TimeSpan.Zero&&recovery.PendingDiagnostic=="pending=false","completed native request clears the stalled state");
        foreach(string code in new[]{"Unreachable","gatt_draining","gatt_stalled","timeout"})
        {
            var diagnosis=BleConnectionDiagnosis.Evaluate(new(){Stage="GATT 服务发现",Outcome=code});
            check(diagnosis.Visible&&diagnosis.Advice.Contains("恢复蓝牙连接"),"recoverable GATT failures have an actionable cached diagnosis");
        }
        var vm=new MainViewModel(previewOnly:true);
        try
        {
            bool prompted=false;
            var launch=await vm.PrepareBluetoothRecoveryAsync(_=>{prompted=true;return valid;});
            check(launch==null&&!prompted&&vm.BluetoothRestartStatus.Contains("未重新初始化"),"UI preview cannot request UAC, stop controllers or cycle a radio");
        }
        finally{await vm.ShutdownAsync();}
    }
    public static void RunUi(Window owner,MainViewModel vm,Action<bool,string> check,Action pump,Action<Window,string> save)
    {
        var source=vm.Pro2Slots[0].InputSource;
        var field=typeof(Pro2BleInputSource).GetField("connectionEvidence",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var old=field.GetValue(source);
        var button=(Button)owner.FindName("RecoverBluetoothConnectionButton");
        try
        {
            foreach(string outcome in new[]{"Unreachable","gatt_draining","gatt_stalled","timeout","no_candidate","channels_missing","connected"})
            {
                field.SetValue(source,new BleConnectionAttemptEvidence{Stage="GATT 服务发现",Address="38:C6:CE:27:FC:2D",Outcome=outcome,Advertisements=1,Candidates=1});
                vm.RefreshCompactTelemetry();pump();
                bool canRecover=outcome is "Unreachable" or "gatt_draining" or "gatt_stalled" or "timeout";
                check(vm.CanOfferBluetoothRecovery==canRecover&&button.IsVisible==canRecover,"homepage recovery action is limited to relevant GATT failures: "+outcome);
                if(outcome=="gatt_stalled")save(owner,"ui-bluetooth-stalled-recovery.png");
            }
        }
        finally{field.SetValue(source,old);vm.RefreshCompactTelemetry();pump();}
        var dialog=new BluetoothRestartWindow(new[]{BluetoothRestartRegressionTests.Target("0BDA","0852")},reinitialize:true){Owner=owner,ShowActivated=false};
        try
        {
            dialog.Show();pump();
            check(((Button)dialog.FindName("ConfirmButton")).Content.ToString()!.Contains("确认恢复并重开助手"),"deep recovery requires explicit confirmation before UAC");
            check(((TextBlock)dialog.FindName("Consequence")).Text.Contains("鼠标")&&((TextBlock)dialog.FindName("RecoveryHint")).Text.Contains("管理员"),
                "confirmation explains Bluetooth peripheral interruption and administrator requirement");
            save(dialog,"ui-bluetooth-reinitialize-confirm.png");
        }
        finally{dialog.Close();}
    }
}
