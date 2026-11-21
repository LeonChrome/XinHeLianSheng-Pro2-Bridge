using Y700Switch2V60Viiper;

internal static class WirelessPro1HandshakePolicyTests
{
    public static void Run(Action<bool,string> check)
    {
        foreach(double target in new[]{133.0,250.0})
        {
            var policy=new WirelessPro1HandshakePolicy(target,1000);policy.Reset(0);
            var protocol=Protocol();
            Input(policy,0);Input(policy,999);
            check(policy.ReportHz==1&&policy.Phase=="FirstContact","Pro1 first contact does not immediately send "+target+"Hz");
            Input(policy,1000);Input(policy,6000);
            check(policy.ReportHz==10&&policy.Phase=="SimpleKeepalive","no-response Pro1 startup leaves 1Hz after one second without jumping to "+target+"Hz");
            policy.ObserveHost(new byte[10]{0x10,0,0,0,0,0,0,0,0,0},protocol,6001,true);
            check(policy.ReportHz==10,"rumble-only output cannot start the handshake");
            policy.ObserveHost(Sub(0x77),protocol,6002,true);
            check(policy.ReportHz==10,"unknown subcommand cannot advance handshake even if caller marks it accepted");
            Send(policy,protocol,6003,2);
            check(policy.ReportHz==15&&policy.Phase=="Initializing","accepted device-info starts the bounded initialization phase");
            Input(policy,11002,GamepadButtons.East);
            check(policy.ReportHz==15,"physical A cannot promote an incomplete ordinary-client handshake");
            Input(policy,11003);
            check(policy.ReportHz==target&&policy.Phase=="FallbackRunning"&&policy.Reason.Contains("5s"),"unknown initialization uses explicit five-second fallback at target "+target);

            policy.Reset(20000);protocol=Protocol();Input(policy,20000);
            Send(policy,protocol,20010,4);
            Input(policy,30000,GamepadButtons.Home);
            check(policy.ReportHz==15&&policy.MenuProtected,"Switch-like 04 handshake cannot time out into high speed");
            Send(policy,protocol,30010,3,0x30);Send(policy,protocol,30020,0x48,1);Send(policy,protocol,30030,0x30,0x10);
            check(policy.ReportHz==15&&policy.Phase=="Initializing","flashing player LED without a low-nibble assignment is not initialized");
            Send(policy,protocol,30040,0x30,1);
            check(policy.ReportHz==15&&policy.Phase=="MenuProtected","initialized Switch-like client stays protected until a new A/B/HOME edge");
            Input(policy,30050,GamepadButtons.Home);
            check(policy.ReportHz==15,"HOME held during initialization is not reinterpreted as a new press");
            Input(policy,30060);Input(policy,30070,GamepadButtons.Home);
            check(policy.ReportHz==target&&!policy.MenuProtected&&policy.Phase=="Running","new physical HOME edge releases Switch-like startup at "+target+"Hz");
            Send(policy,protocol,30080,4);
            Input(policy,30090,GamepadButtons.Home);
            check(policy.ReportHz==15&&policy.MenuProtected,"04 received after Running rearms menu protection; held HOME does not release it");
            Input(policy,30100);Input(policy,30110,GamepadButtons.East);
            check(policy.ReportHz==target&&!policy.MenuProtected,"new physical A edge releases a rearmed menu guard");

            policy.Reset(40000);protocol=Protocol();Input(policy,40000,GamepadButtons.South);
            Send(policy,protocol,40010,4);Initialize(policy,protocol,40020);
            Input(policy,40060,GamepadButtons.South);
            check(policy.ReportHz==15&&policy.MenuProtected,"Reset never treats an opening held B as an exit press");
            Input(policy,40070);Input(policy,40080,GamepadButtons.South);
            check(policy.ReportHz==target&&!policy.MenuProtected,"new physical B press after release works following Reset");
            policy.Reset(50000);
            check(policy.ReportHz==1&&!policy.MenuProtected&&policy.Phase=="FirstContact","each new HID session clears prior Running/menu state");
            protocol=Protocol();Initialize(policy,protocol,50010);
            check(policy.ReportHz==target&&!policy.MenuProtected&&policy.Phase=="Running","ordinary PC initialization reaches requested rate without requiring an A/B/HOME press");

            policy.Reset(60000);protocol=Protocol();Input(policy,60000);
            policy.ObserveHost(Sub(4),protocol,60010,false);
            policy.ObserveHost(Sub(3,0xff),protocol,60020,false);
            check(policy.ReportHz==1&&!policy.MenuProtected,"rejected 04/mode commands do not identify a Switch-like client or start initialization");
            Send(policy,protocol,60030,4);Initialize(policy,protocol,60040);
            Input(policy,60010,GamepadButtons.East);
            check(policy.ReportHz==15,"queued pre-initialization input cannot release a later menu guard");
            Input(policy,60090,GamepadButtons.East);
            check(policy.ReportHz==15,"an old held A cannot become a fresh edge after initialization");
            Input(policy,60100);Input(policy,60110,GamepadButtons.West|GamepadButtons.North);
            check(policy.ReportHz==15,"X/Y are not inferred as menu-exit buttons");
            Input(policy,60120,GamepadButtons.East);
            check(policy.ReportHz==target,"a later genuine A edge still releases the guard after unrelated buttons");
        }
    }
    private static WirelessPro1Protocol Protocol()=>new([1,2,3,4,5,6]);
    private static byte[] Sub(byte command,params byte[] data)
    {byte[] report=new byte[11+data.Length];report[0]=1;report[10]=command;data.CopyTo(report,11);return report;}
    private static void Input(WirelessPro1HandshakePolicy policy,long tick,GamepadButtons buttons=GamepadButtons.None)
    {var state=GamepadState.Neutral();state.Buttons=buttons;policy.ObserveInput(state,tick);}
    private static void Send(WirelessPro1HandshakePolicy policy,WirelessPro1Protocol protocol,long tick,byte command,params byte[] data)
    {
        byte[] report=Sub(command,data);long rejected=protocol.RejectedCommands;
        protocol.HandleOutput(report,GamepadState.Neutral(),tick,1000);
        policy.ObserveHost(report,protocol,tick,protocol.RejectedCommands==rejected);
    }
    private static void Initialize(WirelessPro1HandshakePolicy policy,WirelessPro1Protocol protocol,long tick)
    {Send(policy,protocol,tick,3,0x30);Send(policy,protocol,tick+10,0x48,1);Send(policy,protocol,tick+20,0x30,1);}
}
