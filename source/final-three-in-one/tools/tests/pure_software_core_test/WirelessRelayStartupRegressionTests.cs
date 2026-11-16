using System.Net;
using System.Net.Sockets;
using Y700Switch2V60Viiper;

internal static class WirelessRelayStartupRegressionTests
{
    public static async Task RunAsync(Action<bool,string> check)
    {
        byte[] session=Enumerable.Range(0,16).Select(n=>(byte)n).ToArray();
        IPEndPoint endpoint;
        using(var reservation=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))endpoint=(IPEndPoint)reservation.Client.LocalEndPoint!;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var owner=WirelessRelayLocalIpc.Create();
        var ownerEndpoint=(IPEndPoint)owner.Client.LocalEndPoint!;
        byte[] heartbeat=WirelessRelayDatagram.Encode(session,WirelessRelayMessage.Heartbeat,1,1,[]);
        // Reproduce the actual ordering: owner sends while backend port is
        // still closed, then receives the delayed backend's ready message.
        await owner.SendAsync(heartbeat,endpoint,deadline.Token);
        Task<UdpReceiveResult> pending=owner.ReceiveAsync(deadline.Token).AsTask();
        await Task.Delay(200,deadline.Token);
        using var backend=new UdpClient(endpoint);
        byte[] ready=WirelessRelayDatagram.Encode(session,WirelessRelayMessage.BackendReady,1,2,[]);
        await backend.SendAsync(ready,ownerEndpoint,deadline.Token);
        var received=await pending;
        check(received.RemoteEndPoint.Equals(endpoint)&&WirelessRelayDatagram.TryDecode(received.Buffer,session,out var frame)&&frame!.Kind==WirelessRelayMessage.BackendReady,
            "delayed native bind does not turn local UDP Port Unreachable into fatal 10054");
        await owner.SendAsync(heartbeat,endpoint,deadline.Token);
        var alive=await backend.ReceiveAsync(deadline.Token);
        check(WirelessRelayDatagram.TryDecode(alive.Buffer,session,out frame)&&frame!.Kind==WirelessRelayMessage.Heartbeat,
            "same IPC socket remains usable after the backend becomes ready");
        byte[] otherSession=(byte[])session.Clone();otherSession[0]^=1;
        check(!WirelessRelayDatagram.TryDecode(ready,otherSession,out _),"backend-ready handshake still requires the current private session token");
    }
}
