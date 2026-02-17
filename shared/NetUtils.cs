using System.Net;
using System.Net.Sockets;

namespace FreeAiSsd.Shared;

public static class NetUtils
{
    public static bool IsPortFree(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    public static int FindFreePort(int preferredPort = 11434)
    {
        if (IsPortFree(preferredPort))
        {
            return preferredPort;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
