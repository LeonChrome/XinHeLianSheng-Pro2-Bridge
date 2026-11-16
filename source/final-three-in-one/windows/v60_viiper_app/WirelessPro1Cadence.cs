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
    public long Skipped {get;private set;}
    public long StaleTransitions {get;private set;}
    public bool TrySelect(GamepadState input,out GamepadState selected)
    {
        if(targetHz is <20 or >250||frequency<=0)throw new InvalidOperationException("无线输出节奏无效。");
        bool changed=buttons.HasValue&&(buttons!=input.Buttons||leftTrigger!=(input.L2>0)||rightTrigger!=(input.R2>0));
        buttons=input.Buttons;leftTrigger=input.L2>0;rightTrigger=input.R2>0;
        if(changed)
        {
            if(transitions.Count>=16)throw new InvalidOperationException("输入按键转换积压，停止转发以避免滞留按键。");
            transitions.Enqueue(input.Clone());
        }
        if(nextDue!=0&&input.SourceTimestampTicks<nextDue){Skipped++;selected=default!;return false;}
        while(transitions.TryPeek(out var old)&&
            (old.SourceTimestampTicks<=lastSelectedTicks||(input.SourceTimestampTicks-old.SourceTimestampTicks)/(double)frequency>.1))
        {transitions.Dequeue();StaleTransitions++;}
        selected=transitions.TryDequeue(out var edge)?edge:input;
        if(selected.SourceTimestampTicks<=lastSelectedTicks){selected=default!;return false;}
        lastSelectedTicks=selected.SourceTimestampTicks;long step=(long)Math.Round(frequency/targetHz);
        nextDue=nextDue==0||input.SourceTimestampTicks-nextDue>step?input.SourceTimestampTicks+step:nextDue+step;
        return true;
    }
}
