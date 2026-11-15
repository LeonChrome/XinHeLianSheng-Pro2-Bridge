using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Y700Switch2V60Viiper;

internal static class V327RegressionTests
{
    private static long Tick(double ms) => (long)Math.Round(ms * Stopwatch.Frequency / 1000);
    public static async Task RunAsync(Action<bool,string> check)
    {
        AckOwnership(check);
        HoldGap(check);
        RetryBounds(check);
        await RateClassificationAsync(check);
        await FeedbackIdleAsync(check);
    }

    private static void AckOwnership(Action<bool,string> check)
    {
        byte[] Command(byte cmd, byte sub, uint address=0)
        {
            var data=new byte[16];data[0]=cmd;data[3]=sub;
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12),address);return data;
        }
        var owner=new object();
        var command=Command(2,4,0x13040);
        var waiter=new Pro2CommandAckWaiter(owner,command);
        command[0]=0xff; // The pending identity owns an immutable copy.
        check(!waiter.TryComplete(new object(),Command(2,4,0x13040))&&!waiter.Task.IsCompleted,
            "late ACK from a disposed characteristic cannot complete the current command");
        check(!waiter.TryComplete(owner,Command(0x0b,0))&&!waiter.TryComplete(owner,Command(2,3))&&
            !waiter.TryComplete(owner,Command(2,4,0x13100))&&!waiter.Task.IsCompleted,
            "battery, other subcommand and other flash address cannot satisfy initialization");
        check(!waiter.TryComplete(owner,new byte[3])&&!waiter.Task.IsCompleted,
            "truncated ACK cannot complete an initialization request");
        check(waiter.TryComplete(owner,Command(2,4,0x13040))&&waiter.Task.Result[0]==2&&
            !waiter.TryComplete(owner,Command(2,4,0x13040)),"only the first matching ACK completes its owned request");
        var next=new Pro2CommandAckWaiter(owner,Command(0x15,1));
        check(!next.TryComplete(owner,Command(2,4,0x13040))&&next.TryComplete(owner,Command(0x15,1)),
            "delayed previous command cannot acknowledge the next command");
    }

    private static void HoldGap(Action<bool,string> check)
    {
        var hold=new RearButtonBinding{Enabled=true,Buttons=GamepadButtons.South};
        var repeat=new RearButtonBinding{Enabled=true,Action=RearButtonAction.Repeat,Buttons=GamepadButtons.East,PressMs=40,GapMs=40};
        var config=new RearButtonConfiguration{Ps5=new(){Left=hold,Right=repeat,C=hold with{Buttons=GamepadButtons.L2}}};
        var mapper=new RearButtonMapper();
        GamepadState Apply(double ms,GamepadButtons buttons)=>mapper.Apply(new(){Buttons=buttons},ViiperVirtualMode.DualSenseLike,config,Tick(ms));
        var all=GamepadButtons.PaddleLeft|GamepadButtons.PaddleRight|GamepadButtons.Aux|GamepadButtons.North;
        Apply(0,GamepadButtons.None);
        check(Apply(7.5,all).IsPressed(GamepadButtons.South|GamepadButtons.East|GamepadButtons.L2),"hold, repeat and C trigger start independently");
        check(mapper.TryHandleInputGap(TimeSpan.FromMilliseconds(120),out var gap)&&
            gap.IsPressed(GamepadButtons.South|GamepadButtons.L2|GamepadButtons.North)&&!gap.IsPressed(GamepadButtons.East)&&gap.L2==4095,
            "short gap releases repeat while preserving an active hold, C trigger and physical controls");
        check(!mapper.TryHandleInputGap(TimeSpan.FromMilliseconds(150),out _),"unchanged short-gap holds do not emit repeated safety reports");
        check(Apply(170,all).IsPressed(GamepadButtons.South|GamepadButtons.L2)&&!Apply(178,all).IsPressed(GamepadButtons.East),
            "simple holds survive input resumption while a held repeat requires rearming");
        check(mapper.TryHandleInputGap(TimeSpan.FromMilliseconds(500),out var stale)&&
            !stale.IsPressed(GamepadButtons.South|GamepadButtons.L2)&&stale.IsPressed(GamepadButtons.North),
            "500ms stale input releases mapped holds without releasing physical keys");
        check(!Apply(700,all).IsPressed(GamepadButtons.South|GamepadButtons.East|GamepadButtons.L2),"long-gap recovery cannot replay held macros");
        Apply(708,GamepadButtons.None);
        check(Apply(716,all).IsPressed(GamepadButtons.South|GamepadButtons.East|GamepadButtons.L2),"physical release and press rearm all cancelled mappings");
        var edited=config with{Ps5=config.Ps5 with{Left=hold with{Buttons=GamepadButtons.West}}};
        check(!mapper.Apply(new(){Buttons=all},ViiperVirtualMode.DualSenseLike,edited,Tick(724)).IsPressed(GamepadButtons.West),
            "editing a mapping while held still cannot inject its new assignment");
        mapper.Reset();
        var touch=config with{Ps5=new(){Left=hold with{TouchGesture=Ps5TouchGesture.SwipeUp,PressMs=300}}};
        mapper.Apply(new(),ViiperVirtualMode.DualSenseLike,touch,Tick(0));
        mapper.Apply(new(){Buttons=GamepadButtons.PaddleLeft},ViiperVirtualMode.DualSenseLike,touch,Tick(8));
        check(mapper.TryHandleInputGap(TimeSpan.FromMilliseconds(120),out var stoppedTouch)&&!stoppedTouch.IsPressed(GamepadButtons.South),
            "touch playback is cancelled on a short gap instead of replaying an old gesture");
        check(!mapper.Apply(new(){Buttons=GamepadButtons.PaddleLeft},ViiperVirtualMode.DualSenseLike,touch,Tick(170)).IsPressed(GamepadButtons.South),
            "cancelled touch requires physical rearm");
    }

    private static void RetryBounds(Action<bool,string> check)
    {
        var gate=new BleAdaptiveRetryGate(1000);
        check(gate.AllowsAutomatic(0),"first compatibility measurement remains eligible");
        gate.NoteIncomplete(1000);
        check(!gate.AllowsAutomatic(999)&&!gate.AllowsAutomatic(30999)&&gate.AllowsAutomatic(31000),
            "incomplete measurement retries only after a full 30s backoff");
        gate.NoteIncomplete(31000);
        check(!gate.AllowsAutomatic(1000000),"two incomplete runs cannot form an automatic retry loop");
        gate.Reset();check(gate.AllowsAutomatic(0),"a new physical connection resets the bounded retry budget");
    }

    private static async Task RateClassificationAsync(Action<bool,string> check)
    {
        foreach(bool warm in new[]{false,true})
        {
            await using var source=new Pro2BleInputSource();
            void Set(string field,object value)=>typeof(Pro2BleInputSource).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(source,value);
            typeof(Pro2BleInputSource).GetProperty(nameof(Pro2BleInputSource.IsRunning))!.SetValue(source,true);
            long now=Stopwatch.GetTimestamp();
            Set("transportConnected",true);Set("fastMinimumIntervalAdmitted",warm);Set("connectionIntervalUnits",(ushort)6);
            Set("liveRateProbeTicks",now-5*Stopwatch.Frequency);Set("fd2InputNotifyCount",625u);Set("fd2InputParsedCount",625u);Set("lastParsedNotifyTicks",now);
            source.RecheckLiveRate();
            check(!source.IsHighRateReady&&source.GetRateEvidence().GameplayReady,"125Hz remains playable but is not labelled 133Hz; warm="+warm);
            Set("liveRateProbeTicks",now-5*Stopwatch.Frequency);Set("fd2InputNotifyCount",1291u);Set("fd2InputParsedCount",1291u);Set("lastParsedNotifyTicks",Stopwatch.GetTimestamp());
            source.RecheckLiveRate();check(source.IsHighRateReady,"actual 133Hz passes the same high-rate threshold; warm="+warm);
        }
    }

    private static async Task FeedbackIdleAsync(Action<bool,string> check)
    {
        int timeouts=0;
        void ExceptionObserved(object? sender,FirstChanceExceptionEventArgs args){if(args.Exception is TimeoutException)Interlocked.Increment(ref timeouts);}
        AppDomain.CurrentDomain.FirstChanceException+=ExceptionObserved;
        try
        {
            await using var idle=new FeedbackIdleWait(TimeSpan.FromMilliseconds(5));
            var read=new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            for(int i=0;i<4;i++)check(!await idle.ReadReadyAsync(read.Task,stop.Token)&&!read.Task.IsCompleted,"idle keeps the same pending feedback read "+i);
            read.SetResult([1,2,3]);
            check(await idle.ReadReadyAsync(read.Task,stop.Token)&&read.Task.Result.SequenceEqual(new byte[]{1,2,3}),"feedback arrival is not consumed by an abandoned timeout read");
            check(timeouts==0,"normal PS5 feedback idle allocates no TimeoutException");
            using var cancel=new CancellationTokenSource();cancel.Cancel();
            try { await idle.ReadReadyAsync(new TaskCompletionSource<byte[]>().Task,cancel.Token);check(false,"cancelled idle must stop"); }
            catch(OperationCanceledException){check(true,"feedback stop cancellation interrupts the idle wait");}
        }
        finally {AppDomain.CurrentDomain.FirstChanceException-=ExceptionObserved;}
    }
}
