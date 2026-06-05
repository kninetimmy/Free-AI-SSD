using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace FreeAiSsd.Shared.Discovery;

/// <summary>
/// Companion-side LAN listener. Joins the Runner multicast group and tracks the
/// Runners currently advertising on the subnet, keyed by reachable host:port.
/// The host always comes from the datagram's source address, so it is correct
/// even after a DHCP lease change. Entries older than <see cref="EntryTtl"/>
/// (a few missed beacons) are pruned so a Runner that goes away disappears.
/// </summary>
public sealed class RunnerBeaconListener : IDisposable
{
    /// <summary>Drop a Runner after this long with no beacon (~5 missed at 2s cadence).</summary>
    public static readonly TimeSpan EntryTtl = TimeSpan.FromSeconds(10);

    private readonly Action<string>? _onLog;
    private readonly ConcurrentDictionary<string, Entry> _seen = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private UdpClient? _udp;

    private sealed record Entry(RunnerDiscovery.DiscoveredRunner Runner, DateTimeOffset LastSeenUtc);

    public RunnerBeaconListener(Action<string>? onLog = null) => _onLog = onLog;

    public bool IsRunning => _loop is not null;

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        try
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, RunnerDiscovery.MulticastPort));
            udp.JoinMulticastGroup(RunnerDiscovery.MulticastGroup);
            _udp = udp;
        }
        catch (Exception ex)
        {
            _onLog?.Invoke($"Runner discovery listen failed to start: {ex.Message}");
            _udp?.Dispose();
            _udp = null;
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        var udp = _udp!;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _onLog?.Invoke($"Runner discovery receive error: {ex.Message}");
                break;
            }

            if (!RunnerBeacon.TryParse(result.Buffer, out var beacon) || beacon is null)
            {
                continue;
            }

            var host = result.RemoteEndPoint.Address.ToString();
            var runner = new RunnerDiscovery.DiscoveredRunner(host, beacon.Port, beacon.Name, beacon.Fingerprint);
            _seen[$"{host}:{beacon.Port}"] = new Entry(runner, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>Currently-live Runners, stalest entries pruned.</summary>
    public IReadOnlyList<RunnerDiscovery.DiscoveredRunner> CurrentRunners()
    {
        var cutoff = DateTimeOffset.UtcNow - EntryTtl;
        var live = new List<RunnerDiscovery.DiscoveredRunner>();
        foreach (var kvp in _seen)
        {
            if (kvp.Value.LastSeenUtc < cutoff)
            {
                _seen.TryRemove(kvp.Key, out _);
                continue;
            }

            live.Add(kvp.Value.Runner);
        }

        return live;
    }

    /// <summary>
    /// The Runner this companion should connect to right now (fingerprint match
    /// preferred, sole-runner fallback), or null if none/ambiguous. See
    /// <see cref="RunnerDiscovery.SelectBestMatch"/>.
    /// </summary>
    public RunnerDiscovery.DiscoveredRunner? BestMatch(string? myFingerprint)
        => RunnerDiscovery.SelectBestMatch(CurrentRunners(), myFingerprint);

    public void Dispose()
    {
        _cts?.Cancel();
        try
        {
            _udp?.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignore teardown races
        }

        _cts?.Dispose();
        _cts = null;
        _loop = null;
        _udp = null;
    }
}
