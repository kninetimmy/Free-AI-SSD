using System.Diagnostics;
using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.PrepApp;

/// <summary>
/// Manages a temporary Ollama server process for the duration of model pull operations.
///
/// Ollama CLI commands like "pull" and "rm" require a running server. If none is found,
/// Ollama auto-starts an uncontrolled background server process that can interfere with
/// the host system (opens tray icons, persists after the app exits, competes for ports).
///
/// This class explicitly starts "ollama serve" on a random available port with
/// CreateNoWindow=true, waits for it to become healthy, and kills it on disposal.
/// Callers should pass the returned Host string as OLLAMA_HOST to all Ollama CLI commands.
/// </summary>
public sealed class OllamaServerHandle : IOllamaServerHandle
{
    private readonly Process _process;
    private readonly Action<string> _onLog;
    private bool _disposed;

    /// <summary>The host:port string to pass as OLLAMA_HOST to Ollama CLI commands.</summary>
    public string Host { get; }

    private OllamaServerHandle(Process process, string host, Action<string> onLog)
    {
        _process = process;
        Host = host;
        _onLog = onLog;
    }

    /// <summary>
    /// Starts a temporary Ollama server and waits for it to become healthy.
    /// The server listens on a random available port to avoid conflicts with any
    /// system-installed Ollama instance.
    /// </summary>
    /// <param name="ollamaExe">Path to the Ollama executable on the SSD.</param>
    /// <param name="modelsRoot">Path to the SSD's models directory (set as OLLAMA_MODELS).</param>
    /// <param name="onLog">Callback for status messages.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A handle that must be disposed to stop the server.</returns>
    public static async Task<OllamaServerHandle> StartAsync(
        string ollamaExe, string modelsRoot, Action<string> onLog, CancellationToken ct)
    {
        var port = FindAvailablePort();
        var host = $"127.0.0.1:{port}";

        onLog($"Starting temporary Ollama server on {host}...");

        var startInfo = new ProcessStartInfo
        {
            FileName = ollamaExe,
            WorkingDirectory = Path.GetDirectoryName(ollamaExe)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("serve");
        startInfo.Environment["OLLAMA_MODELS"] = modelsRoot;
        startInfo.Environment["OLLAMA_HOST"] = host;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();

        // Stream stdout/stderr through onLog (prefixed `[ollama serve]`)
        // on background threads. Pre-MAC28 we discarded output, which made
        // first-launch failures (Gatekeeper holds, GPU runner discovery
        // errors, port collisions) invisible — the only signal was a
        // generic "did not become healthy" timeout. Routing through onLog
        // surfaces ollama's own startup log lines in the PrepApp UI so
        // the next failure mode is diagnosable from the staged SSD log.
        // Must use Task.Run because ConsumeAsync starts synchronously and
        // we must not block the UI thread while waiting for the first byte.
        _ = Task.Run(() => ConsumeAsync(process.StandardOutput, onLog, "stdout"), ct);
        _ = Task.Run(() => ConsumeAsync(process.StandardError, onLog, "stderr"), ct);

        // Wait for the server to become healthy before returning.
        // If the health check fails, kill the process immediately so it doesn't
        // leak as an orphaned background server.
        try
        {
            await WaitForHealthyAsync(host, process, onLog, ct);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
            finally
            {
                process.Dispose();
            }

            throw; // Re-throw the original health-check failure.
        }

        onLog($"Temporary Ollama server ready on {host}.");
        return new OllamaServerHandle(process, host, onLog);
    }

    /// <summary>
    /// Stops the temporary server by killing its entire process tree.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                _onLog("Stopping temporary Ollama server...");
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch
        {
            // Best-effort cleanup; the process may have already exited.
        }
        finally
        {
            _process.Dispose();
        }
    }

    /// <summary>
    /// Finds a random available TCP port by binding to port 0 and reading the assigned port.
    /// </summary>
    private static int FindAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Polls the server's /api/tags endpoint until it responds with 200 OK.
    /// Throws OperationCanceledException if cancelled, or InvalidOperationException
    /// if the server process exits before becoming healthy.
    /// </summary>
    private static async Task WaitForHealthyAsync(
        string host, Process process, Action<string> onLog, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var url = $"http://{host}/api/tags";

        // MAC28: bumped from 30 (=15s) to 120 (=60s). The 15s budget was
        // tight even on Windows — on Mac, the first-launch path includes
        // Gatekeeper signature verification (Apple OCSP roundtrip on a
        // freshly-staged inner Ollama binary) and the Go server's runner
        // discovery (`Dynamic LLM libraries: [metal cpu_avx cpu_avx2]`).
        // v1.3.8 mac field test reproduced the timeout while the
        // Gatekeeper "Verifying 'Ollama'" modal was still visible. 60s
        // gives the cold-start chain enough headroom without making
        // genuinely-broken cases meaningfully slower to diagnose.
        const int maxAttempts = 120;
        for (var i = 0; i < maxAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (process.HasExited)
                throw new InvalidOperationException(
                    $"Ollama server process exited unexpectedly with code {process.ExitCode}.");

            try
            {
                using var response = await http.GetAsync(url, ct);
                if (response.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch (HttpRequestException)
            {
                // Server not ready yet.
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // HTTP timeout; server not ready yet.
            }

            await Task.Delay(500, ct);
        }

        throw new TimeoutException(
            $"Ollama server on {host} did not become healthy within {maxAttempts * 500 / 1000} seconds.");
    }

    /// <summary>
    /// Reads lines from <paramref name="reader"/> and routes each non-empty
    /// line through <paramref name="onLog"/> tagged
    /// <c>[ollama serve <paramref name="streamLabel"/>] &lt;line&gt;</c>.
    /// Uses ReadLineAsync (returns null at EOF) instead of the EndOfStream
    /// property, which is synchronous and blocks the calling thread when no
    /// data is available. The <c>internal static</c> shape exists so tests
    /// can drive synthetic streams without spawning a real ollama process.
    /// </summary>
    internal static async Task ConsumeAsync(StreamReader reader, Action<string> onLog, string streamLabel)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    onLog($"[ollama serve {streamLabel}] {line}");
                }
                catch
                {
                    // A misbehaving onLog must not stop the consumer — the
                    // primary purpose of this loop is to drain the pipe so
                    // the child process doesn't block on a full stdout/stderr
                    // buffer.
                }
            }
        }
        catch
        {
            // Process exited or stream closed.
        }
    }
}
