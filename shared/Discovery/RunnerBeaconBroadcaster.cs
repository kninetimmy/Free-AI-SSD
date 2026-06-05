using System.Net;
using System.Net.Sockets;

namespace FreeAiSsd.Shared.Discovery;

/// <summary>
/// Runner-side LAN beacon. While running, multicasts a <see cref="RunnerBeacon"/>
/// every <see cref="RunnerDiscovery.BroadcastInterval"/> so companions can find
/// this Runner without a hand-typed IP. Best-effort: send failures (no route, a
/// downed NIC) are swallowed — discovery is a convenience, never a hard
/// dependency of the API. Started only when the API is exposed on the LAN.
/// </summary>
public sealed class RunnerBeaconBroadcaster : IAsyncDisposable
{
    private readonly Action<string>? _onLog;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public RunnerBeaconBroadcaster(Action<string>? onLog = null) => _onLog = onLog;

    public bool IsRunning => _loop is not null;

    public void Start(string instanceName, int port, string apiKeyFingerprint)
    {
        if (IsRunning)
        {
            return;
        }

        var beacon = new RunnerBeacon(instanceName, port, apiKeyFingerprint);
        var payload = beacon.Serialize();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => BroadcastLoopAsync(payload, _cts.Token));
    }

    private async Task BroadcastLoopAsync(byte[] payload, CancellationToken ct)
    {
        var endpoint = new IPEndPoint(RunnerDiscovery.MulticastGroup, RunnerDiscovery.MulticastPort);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Ttl = 1; // keep the beacon on the local subnet only

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await udp.SendAsync(payload, payload.Length, endpoint).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _onLog?.Invoke($"Runner beacon send failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(RunnerDiscovery.BroadcastInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            if (_loop is not null)
            {
                await _loop.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _onLog?.Invoke($"Runner beacon stop error: {ex.Message}");
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _loop = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
