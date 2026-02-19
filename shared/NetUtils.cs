using System.Net;
using System.Net.Sockets;

namespace FreeAiSsd.Shared;

/// <summary>
/// Network utility methods for port availability checking and free port discovery.
/// Used to find an available port for the local Ollama server instance.
/// </summary>
public static class NetUtils
{
    /// <summary>
    /// Tests whether a specific port is available for listening on the loopback address.
    /// Briefly opens and closes a TCP listener to determine availability.
    /// </summary>
    /// <param name="port">The TCP port number to check.</param>
    /// <returns>True if the port is free and can be bound; false if it is in use.</returns>
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

    /// <summary>
    /// Finds a free TCP port, preferring the specified port number.
    /// If the preferred port is occupied, asks the OS for any available port.
    /// </summary>
    /// <param name="preferredPort">The port to try first (default: 11434, Ollama's default port).</param>
    /// <returns>An available port number.</returns>
    public static int FindFreePort(int preferredPort = 11434)
    {
        if (IsPortFree(preferredPort))
        {
            return preferredPort;
        }

        // Bind to port 0 to let the OS assign any available ephemeral port.
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
