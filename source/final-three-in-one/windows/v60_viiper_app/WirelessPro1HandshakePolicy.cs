using System;

namespace Y700Switch2V60Viiper;

// Caller serializes this policy with WirelessPro1Protocol. Time is supplied by
// the caller; this class does not read a clock, query a device or perform IO.
public sealed class WirelessPro1HandshakePolicy
{
    private const GamepadButtons ExitButtons=GamepadButtons.East|GamepadButtons.South|GamepadButtons.Home;
    private readonly double requestedHz;
    private readonly long frequency;
    private bool hasEpoch,hostSeen,initialized,switchLike,menuExited,inputBaseline;
    private long resetTicks,clockTicks,firstHostTicks,initializedTicks,menuArmedTicks,lastInputTicks;
    private GamepadButtons previousButtons;

    public double ReportHz {get;private set;}=1;
    public string Phase {get;private set;}="FirstContact";
    public string Reason {get;private set;}="awaiting_first_accepted_host_command";
    public bool MenuProtected=>switchLike&&!menuExited;

    public WirelessPro1HandshakePolicy(double requestedHz,long frequency)
    {
        if(!double.IsFinite(requestedHz)||requestedHz<=0||requestedHz>250)throw new ArgumentOutOfRangeException(nameof(requestedHz));
        if(frequency<=0)throw new ArgumentOutOfRangeException(nameof(frequency));
        this.requestedHz=requestedHz;this.frequency=frequency;
    }

    public void Reset(long ticks)
    {
        hasEpoch=true;resetTicks=clockTicks=ticks;firstHostTicks=initializedTicks=menuArmedTicks=lastInputTicks=0;
        hostSeen=initialized=switchLike=menuExited=inputBaseline=false;previousButtons=GamepadButtons.None;
        Update();
    }

    // accepted must describe the protocol handler's semantic result, not the
    // USB/HCI write status. Rumble-only output is not a handshake response.
    public void ObserveHost(ReadOnlySpan<byte> report,WirelessPro1Protocol protocol,long ticks,bool accepted)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        Advance(ticks);
        if(ticks<resetTicks||!accepted||report.Length<11||report[0]!=1||!KnownSubcommand(report[10])){Update();return;}
        if(!hostSeen){hostSeen=true;firstHostTicks=clockTicks;}
        if(report[10]==4){switchLike=true;menuExited=false;menuArmedTicks=clockTicks;}
        if(!initialized&&protocol.InputMode!=0x3f&&protocol.VibrationEnabled&&(protocol.PlayerLights&15)!=0)
        {initialized=true;initializedTicks=clockTicks;}
        Update();
    }

    // Observe each original physical report before key remapping. The first
    // report establishes held keys; it can never supply a synthetic press edge.
    public void ObserveInput(GamepadState original,long ticks)
    {
        ArgumentNullException.ThrowIfNull(original);
        Advance(ticks);
        if(ticks<resetTicks||(inputBaseline&&ticks<=lastInputTicks)){Update();return;}
        GamepadButtons current=original.Buttons&ExitButtons;
        GamepadButtons pressed=inputBaseline?current&~previousButtons:GamepadButtons.None;
        inputBaseline=true;previousButtons=current;lastInputTicks=ticks;
        if(initialized&&MenuProtected&&pressed!=GamepadButtons.None&&ticks>menuArmedTicks&&ticks>=initializedTicks)
            menuExited=true;
        Update();
    }

    private void Advance(long ticks)
    {
        if(!hasEpoch)Reset(ticks);
        if(ticks>clockTicks)clockTicks=ticks;
    }
    private double Elapsed(long start)=>((double)clockTicks-start)/frequency;
    private void Set(double hz,string phase,string reason){ReportHz=hz;Phase=phase;Reason=reason;}
    private void Update()
    {
        if(!hostSeen)
        {
            if(Elapsed(resetTicks)>=1)Set(10,"SimpleKeepalive","no_accepted_host_command_after_1s; simple_keepalive_10hz");
            else Set(1,"FirstContact","awaiting_first_accepted_host_command");
        }
        else if(MenuProtected)
            Set(15,initialized?"MenuProtected":"Initializing",initialized?
                "switch_like_signature_sub04; awaiting_new_physical_A_B_HOME_edge; menu_exit_is_inferred":
                "switch_like_signature_sub04; awaiting_initialization; timeout_does_not_remove_menu_protection");
        else if(initialized)
            Set(requestedHz,"Running",switchLike?
                "initialization_complete; new_physical_A_B_HOME_edge; menu_exit_is_inferred":
                "initialization_complete; pc_client_without_sub04_signature");
        else if(Elapsed(firstHostTicks)>=5)
            Set(requestedHz,"FallbackRunning","unknown_client_initialization_timeout_5s; requested_rate_fallback_unverified");
        else Set(15,"Initializing","accepted_host_subcommand; initializing_15hz");
    }
    private static bool KnownSubcommand(byte command)=>command is 1 or 2 or 3 or 4 or 8 or 0x10 or 0x21 or 0x22 or 0x30 or 0x31 or 0x38 or 0x40 or 0x41 or 0x48 or 0x50;
}
