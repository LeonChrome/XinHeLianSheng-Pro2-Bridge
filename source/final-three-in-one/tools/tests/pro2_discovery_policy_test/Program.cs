using System.Diagnostics;
using System.Text.Json;
using Y700Switch2V60Viiper;

var checks=0;
var timing=new List<object>();
void Check(bool value,string description) { if(!value)throw new InvalidOperationException(description);checks++; }
var target="38:C6:CE:27:FC:2D";
var another="A4:C1:E8:D3:A3:ED";
var other="00:11:22:33:44:55";
var policy=new Pro2DiscoveryPolicy(new[]{target},new[]{other},false);
Check(Pro2DiscoveryPolicy.AllowsEarlyCompletion(Array.Empty<string>(),false),"normal connection acceleration enabled");
Check(!Pro2DiscoveryPolicy.AllowsEarlyCompletion(new[]{"--BLE-DIAGNOSTIC"},false),"CLI diagnostic retains complete scan");
Check(!Pro2DiscoveryPolicy.AllowsEarlyCompletion(Array.Empty<string>(),true),"isolated custom experiment retains complete scan");
Check(policy.IsFreshSavedTarget(target.ToLowerInvariant()),"saved addresses are case insensitive");
Check(!policy.IsFreshSavedTarget(another),"unseen target is not a saved address");
Check(policy.EarlyCompletionReason(target,"",false)=="saved_address_advertisement","fresh unnamed saved advertising target qualifies");
Check(policy.EarlyCompletionReason(other,"Pro2 Controller",true)==null,"excluded controller cannot end scan");
Check(new Pro2DiscoveryPolicy(new[]{other},new[]{other},false).EarlyCompletionReason(other,"Pro2",true)==null,"excluded wins over preferred");
Check(policy.EarlyCompletionReason(another,"Pro2 Controller",true)=="named_pro2_advertisement","explicit new name and manufacturer qualify");
Check(policy.EarlyCompletionReason(another,"Nintendo Pro 2 Controller",true)!=null,"spaced Pro 2 qualifies");
foreach(var name in new[]{"", "Switch", "Nintendo", "Pro Controller", "Controller", "Pro2 headset"})
    Check(policy.EarlyCompletionReason(another,name,false)==null,"name without Nintendo flag cannot shorten scan: "+name);
foreach(var name in new[]{"", "Switch", "Nintendo", "Pro Controller", "Controller"})
    Check(policy.EarlyCompletionReason(another,name,true)==null,"broad Nintendo evidence cannot shorten scan: "+name);
foreach(var name in new[]{"Pro20", "Impro2", "Pro 20", "Pro2Controller"})
    Check(policy.EarlyCompletionReason(another,name,true)==null,"Pro2 token boundaries exclude other products: "+name);
var strict=new Pro2DiscoveryPolicy(new[]{target},Array.Empty<string>(),true);
Check(strict.EarlyCompletionReason(another,"Pro2 Controller",true)==null,"strict preferred target blocks another valid controller");
Check(strict.EarlyCompletionReason(target,"",false)!=null,"strict preferred target can finish early");
Check(!new Pro2DiscoveryPolicy(new[]{target},new[]{target},false).IsFreshSavedTarget(target),"excluded known anonymous address not promoted");

TaskCompletionSource<T> Signal<T>()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
async Task ThrowsCancellation(Task task,string name)
{
    try { await task;throw new InvalidOperationException(name+": cancellation was ignored"); }
    catch(OperationCanceledException){checks++;}
}
async Task TimedScan(string name,Func<Task<string>> run,double minMs,double maxMs,string expected)
{
    var sw=Stopwatch.StartNew();var result=await run();double elapsed=sw.Elapsed.TotalMilliseconds;
    Check(result==expected,name+": unexpected reason "+result);
    Check(elapsed>=minMs && elapsed<maxMs,name+": duration "+elapsed+"ms");
    timing.Add(new{name,elapsed_ms=Math.Round(elapsed,2),reason=result});
}
await TimedScan("no early evidence retains full discovery budget",()=>Pro2DiscoveryPolicy.WaitForScanCompletionAsync(
    TimeSpan.FromMilliseconds(100),Signal<string>().Task,Signal<bool>().Task,CancellationToken.None),85,2000,"full_window");
await TimedScan("saved address already seen uses short collection",()=>Pro2DiscoveryPolicy.WaitForScanCompletionAsync(
    TimeSpan.FromSeconds(8),Task.FromResult("saved_address_advertisement"),Signal<bool>().Task,CancellationToken.None),220,2000,"saved_address_advertisement");
await TimedScan("candidate near deadline never extends scan budget",()=>Pro2DiscoveryPolicy.WaitForScanCompletionAsync(
    TimeSpan.FromMilliseconds(80),Task.FromResult("saved_address_advertisement"),Signal<bool>().Task,CancellationToken.None),65,2000,"full_window");
await TimedScan("stopped watcher releases wait",()=>Pro2DiscoveryPolicy.WaitForScanCompletionAsync(
    TimeSpan.FromSeconds(8),Signal<string>().Task,Task.CompletedTask,CancellationToken.None),0,1000,"watcher_stopped");
{
    var candidate=Signal<string>();var stopped=Signal<bool>();
    var scan=Pro2DiscoveryPolicy.WaitForScanCompletionAsync(TimeSpan.FromSeconds(8),candidate.Task,stopped.Task,CancellationToken.None);
    await Task.Delay(30);Check(!scan.IsCompleted,"scan must wait for evidence");
    Check(candidate.TrySetResult("named_pro2_advertisement"),"first candidate owns completion");
    Check(!candidate.TrySetResult("other"),"subsequent advertisements cannot replace completion");
    await Task.Delay(50);Check(!scan.IsCompleted,"bounded collection window preserves scan responses");
    stopped.SetResult(true);Check(await scan=="watcher_stopped","stop during collection releases wait");
}
using(var cts=new CancellationTokenSource())
{
    cts.Cancel();var sw=Stopwatch.StartNew();
    await ThrowsCancellation(Pro2DiscoveryPolicy.WaitForScanCompletionAsync(TimeSpan.FromSeconds(8),Task.FromResult("candidate"),Task.CompletedTask,cts.Token),"pre-cancel");
    Check(sw.ElapsedMilliseconds<500,"pre-cancel must not wait for full or collect budget");
}
using(var cts=new CancellationTokenSource())
{
    var scan=Pro2DiscoveryPolicy.WaitForScanCompletionAsync(TimeSpan.FromSeconds(8),Signal<string>().Task,Signal<bool>().Task,cts.Token);
    cts.Cancel();await ThrowsCancellation(scan,"cancel during discovery");
}
using(var cts=new CancellationTokenSource())
{
    var scan=Pro2DiscoveryPolicy.WaitForScanCompletionAsync(TimeSpan.FromSeconds(8),Task.FromResult("candidate"),Signal<bool>().Task,cts.Token);
    await Task.Delay(30);cts.Cancel();await ThrowsCancellation(scan,"cancel during collection");
}

// Actual disconnect wake primitive used by the health guard; no WhenAny orphan
// waiter is created, and one coalesced notification cannot start multiple retries.
var wake=new InputFrameSignal();
wake.Notify();
var signalWatch=Stopwatch.StartNew();
Check(await wake.WaitAsync(TimeSpan.FromMilliseconds(750),CancellationToken.None),"disconnect observed before wait is retained");
Check(signalWatch.ElapsedMilliseconds<500,"preexisting disconnect signal wakes immediately");
var pending=wake.WaitAsync(TimeSpan.FromMilliseconds(750),CancellationToken.None);
Check(!pending.IsCompleted,"no signal before new disconnect");wake.Notify();
Check(await pending,"disconnect while guard waits wakes it");
for(int i=0;i<100;i++)wake.Notify();
Check(await wake.WaitAsync(TimeSpan.Zero,CancellationToken.None),"many callbacks coalesce into one pending wake");
Check(!await wake.WaitAsync(TimeSpan.Zero,CancellationToken.None),"coalesced callbacks cannot trigger another reconnect");
using(var cts=new CancellationTokenSource())
{
    var wait=wake.WaitAsync(TimeSpan.FromSeconds(5),cts.Token);cts.Cancel();await ThrowsCancellation(wait,"cancel guard wait");
    wake.Notify();Check(await wake.WaitAsync(TimeSpan.Zero,CancellationToken.None),"cancelled wait cannot steal the next signal");
}
signalWatch.Restart();
Check(!await wake.WaitAsync(TimeSpan.FromMilliseconds(750),CancellationToken.None),"no disconnect uses existing guard timeout");
Check(signalWatch.Elapsed.TotalMilliseconds>=700,"no-signal live guard retains 750ms timeout");

var report=new{success=true,checks,timing,hardware_tested=false,bluetooth_operations=0};
var text=JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true});
if(args.Length>0){Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);File.WriteAllText(args[0],text);}
Console.WriteLine(text);
