using Y700Switch2V60Viiper;

internal static class WirelessPro1PhaseCadenceTests
{
    public static void Run(Action<bool,string> check)
    {
        const long frequency=1_000_000,epoch=frequency;
        foreach(double target in new[]{133.0,250.0})
        {
            var policy=new WirelessPro1HandshakePolicy(target,frequency);policy.Reset(epoch);
            var protocol=new WirelessPro1Protocol([1,2,3,4,5,6]);
            var cadence=new WirelessPro1Cadence(target,frequency);
            int initial=0,menu=0,playing=0;long last=0;bool fresh=true;
            for(int ms=0;ms<10000;ms+=4)
            {
                long stamp=epoch+ms*1000L;
                if(ms==1000)
                    foreach(byte[] cmd in new byte[][]{[1,0,0,0,0,0,0,0,0,0,4],[1,0,0,0,0,0,0,0,0,0,3,0x30],[1,0,0,0,0,0,0,0,0,0,0x48,1],[1,0,0,0,0,0,0,0,0,0,0x30,1]})
                    {protocol.HandleOutput(cmd,GamepadState.Neutral(),stamp,frequency);policy.ObserveHost(cmd,protocol,stamp,true);}
                var physical=GamepadState.Neutral();physical.SourceTimestampTicks=stamp;
                if(ms==6000)physical.Buttons=GamepadButtons.East;
                policy.ObserveInput(physical,stamp);
                if(!cadence.TrySelect(physical,out var selected,policy.ReportHz))continue;
                fresh&=selected.SourceTimestampTicks>last&&selected.SourceTimestampTicks<=stamp;
                last=selected.SourceTimestampTicks;
                if(ms<1000)initial++;else if(ms<6000)menu++;else playing++;
            }
            check(initial<=2&&menu is >=74 and <=76,"Switch-like handshake/menu cap applies to noisy 250Hz physical input before target "+target);
            check(Math.Abs(playing/4.0-target)<1.5,"new physical A edge releases the cadence to requested target "+target);
            check(fresh,"every selected phase report has a distinct real source timestamp at target "+target);
        }
        var pressure=new WirelessPro1Cadence(133,frequency);
        for(int ms=0;ms<1500;ms+=4)
        {
            var state=GamepadState.Neutral();state.SourceTimestampTicks=epoch+ms*1000L;
            state.Buttons=(ms/4)%2==0?GamepadButtons.East:GamepadButtons.None;
            pressure.TrySelect(state,out _,1);
        }
        check(pressure.Resyncs>0,"rapid real button transitions during low-rate pairing resync instead of stopping the radio");
        var released=GamepadState.Neutral();released.SourceTimestampTicks=epoch+2*frequency;
        check(pressure.TrySelect(released,out var selectedRelease,133)&&selectedRelease.Buttons==GamepadButtons.None,
            "raising cadence discards old transition backlog and immediately sends the current released state");
        check(pressure.StaleTransitions>0,"pairing transition expiry and resync are counted for diagnostics");
    }
}
