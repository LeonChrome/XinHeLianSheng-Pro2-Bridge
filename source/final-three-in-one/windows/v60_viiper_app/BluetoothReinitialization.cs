using System.Diagnostics;

namespace Y700Switch2V60Viiper;

public sealed record BluetoothReinitializationResult(int? DisableExit, bool DisabledConfirmed, bool EnableAttempted, int? EnableExit, string Error)
{
    public bool NeedsReboot=>DisableExit==3010||EnableExit==3010;
    public bool SequenceSucceeded=>DisableExit==0&&DisabledConfirmed&&EnableAttempted&&EnableExit==0&&!NeedsReboot&&Error.Length==0;
}

public static class BluetoothReinitialization
{
    // Recovery enable is deliberately independent of the cancelled disable token.
    // It also runs after a partial failure/timeout: the radio was enabled initially.
    public static async Task<BluetoothReinitializationResult> RunAsync(Func<Task<int>> disable,Func<Task<bool>> isDisabled,
        Func<Task<int>> enable,Func<Task> settle,CancellationToken token)
    {
        int? down=null,up=null;bool attempted=false,confirmed=false,enableAttempted=false;string error="";
        try
        {
            token.ThrowIfCancellationRequested();attempted=true;
            down=await disable();confirmed=await isDisabled();
            if(down==0&&confirmed){token.ThrowIfCancellationRequested();await settle();}
        }
        catch(Exception ex){error=ex.GetType().Name+": "+ex.Message;}
        finally
        {
            if(attempted)
            {
                enableAttempted=true;
                try{up=await enable();}catch(Exception ex){error+="; enable: "+ex.Message;}
            }
        }
        return new(down,confirmed,enableAttempted,up,error);
    }
}

public sealed record BluetoothRecoveryLaunch(string Operation,string PnpId,string InterfaceId,int SlotMask)
{
    public static BluetoothRecoveryLaunch? Parse(IReadOnlyList<string> args)
    {
        int index=args.ToList().IndexOf("--ble-recovery-ticket");
        if(index!=0||args.Count!=5||!Guid.TryParseExact(args[index+1],"N",out _)||
            !int.TryParse(args[index+4],out int mask)||mask is <1 or >15)return null;
        var request=new BluetoothRecoveryLaunch(args[index+1],args[index+2],args[index+3],mask);
        return BluetoothRestartPolicy.IsExactRadioTarget(request.Target)?request:null;
    }
    public BluetoothRestartTarget Target=>new("蓝牙适配器",PnpId,InterfaceId,"","",false);
    public string[] Arguments=>["--ble-recovery-ticket",Operation,PnpId,InterfaceId,SlotMask.ToString()];
    public static string ParentIdentity(Process parent)=>parent.Id+":"+parent.StartTime.ToUniversalTime().Ticks;
    public static bool IsParentToken(string token)
    {
        var parts=token.Split(':');return parts.Length==2&&int.TryParse(parts[0],out int pid)&&pid>0&&
            long.TryParse(parts[1],out long ticks)&&ticks>DateTime.MinValue.Ticks&&ticks<=DateTime.MaxValue.Ticks;
    }
}
