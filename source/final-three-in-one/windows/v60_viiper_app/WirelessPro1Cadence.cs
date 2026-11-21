using System;
using System.Collections.Generic;

namespace Y700Switch2V60Viiper;

// Called only when a real physical report arrives: never a timer/old-state
// generator. Retain button/trigger transitions while selecting native cadence.
public sealed class WirelessPro1Cadence(double targetHz,long frequency)
{
    private readonly Queue<GamepadState> transitions=new();
    private long nextDue,lastSelectedTicks;
    private GamepadButtons? buttons;
    private bool leftTrigger,rightTrigger;
    private double activeHz;
    public long Skipped {get;private set;}
    public long StaleTransitions {get;private set;}
    public long Resyncs {get;private set;}
    public bool TrySelect(GamepadState input,out GamepadState selected,double? rateOverride=null)
    {
        double hz=rateOverride??targetHz;
        if(!double.IsFinite(targetHz)||targetHz is <20 or >250||!double.IsFinite(hz)||hz is <1 or >250||frequency<=0)throw new InvalidOperationException("无线输出节奏无效。");
        if(activeHz!=hz){StaleTransitions+=transitions.Count;transitions.Clear();nextDue=0;activeHz=hz;}
        while(transitions.TryPeek(out var old)&&
            (old.SourceTimestampTicks<=lastSelectedTicks||(input.SourceTimestampTicks-old.SourceTimestampTicks)/(double)frequency>.1))
        {transitions.Dequeue();StaleTransitions++;}
        bool changed=buttons.HasValue&&(buttons!=input.Buttons||leftTrigger!=(input.L2>0)||rightTrigger!=(input.R2>0));
        buttons=input.Buttons;leftTrigger=input.L2>0;rightTrigger=input.R2>0;
        if(changed)
        {
            if(transitions.Count>=16){StaleTransitions+=transitions.Count;transitions.Clear();Resyncs++;}
            transitions.Enqueue(input.Clone());
        }
        if(nextDue!=0&&input.SourceTimestampTicks<nextDue){Skipped++;selected=default!;return false;}
        selected=transitions.TryDequeue(out var edge)?edge:input;
        if(selected.SourceTimestampTicks<=lastSelectedTicks){selected=default!;return false;}
        lastSelectedTicks=selected.SourceTimestampTicks;long step=(long)Math.Round(frequency/hz);
        nextDue=nextDue==0||input.SourceTimestampTicks-nextDue>step?input.SourceTimestampTicks+step:nextDue+step;
        return true;
    }
}
