using System.Net;
using System.Net.Sockets;

namespace Y700Switch2V60Viiper;

public static class WirelessRelayLocalIpc
{
    public static UdpClient Create()
    {
        var client=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        try
        {
            // A first heartbeat can precede the native process's bind(). On
            // Windows its ICMP Port Unreachable otherwise surfaces as 10054
            // on ReceiveAsync and used to abort radio firmware initialization.
            client.Client.IOControl(unchecked((int)0x9800000C),new byte[4],null); // SIO_UDP_CONNRESET = FALSE
            return client;
        }
        catch{client.Dispose();throw;}
    }
}
