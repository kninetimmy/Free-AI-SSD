using System.Diagnostics;
using FreeAiSsd.Shared;

namespace FreeAiSsd.Runner.Services;

public sealed class OllamaLifecycleService : IOllamaLifecycleService
{
    private readonly SsdLogger? _logger;
    private Process? _ollama;

    public OllamaLifecycleService(SsdLogger? logger)
    {
        _logger = logger;
    }

    public bool IsRunning => _ollama is { HasExited: false };
    public int? CurrentPort { get; private set; }
    public string? CurrentHost => CurrentPort.HasValue ? $"127.0.0.1:{CurrentPort.Value}" : null;

    public event Action<string>? LogMessage;
    public event Action? ProcessExited;

    public (bool IsTrusted, string Message) ValidateTrust(string ssdRoot)
    {
        var gate = OllamaPackageTrustPolicy.ValidateExecutionAttestation(ssdRoot);
        return (gate.IsTrusted, gate.Message);
    }

    public OllamaStartResult Start(PortableConfig config, string ssdRoot)
    {
        if (_ollama is { HasExited: false })
        {
            return new OllamaStartResult(false, "Ollama is already running.");
        }

        var ollamaExe = Path.Combine(ssdRoot, config.OllamaRelativePath);
        if (!File.Exists(ollamaExe))
        {
            return new OllamaStartResult(false, "ollama.exe missing in staged tools folder.");
        }

        // A runner that crashed via an unhandled exception never ran Stop(), so its
        // ollama (and llama-server children) outlive it — they hold the preferred
        // port (forcing this launch to climb) and contend for the GPU. Reap any such
        // orphan rooted at THIS SSD's tools folder before starting a fresh one.
        KillStaleInstances(ollamaExe);

        int port;
        try
        {
            port = ResolvePort(config.OllamaPort);
        }
        catch (Exception ex)
        {
            return new OllamaStartResult(false, $"Unable to find a free port: {ex.Message}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ollamaExe,
            Arguments = "serve",
            WorkingDirectory = Path.GetDirectoryName(ollamaExe)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.Environment["OLLAMA_MODELS"] = Path.Combine(ssdRoot, SsdLayout.Models);
        startInfo.Environment["OLLAMA_HOST"] = $"127.0.0.1:{port}";
        startInfo.Environment["OLLAMA_ORIGINS"] = "http://127.0.0.1,http://localhost";

        var gpuDecision = GpuAccelerationPolicy.ResolveFor(SystemResources.GetGpuVendor(), config.PreferredCompute);
        foreach (var kvp in gpuDecision.EnvironmentVariables)
        {
            startInfo.Environment[kvp.Key] = kvp.Value;
        }
        _logger?.Info($"Ollama acceleration backend: {gpuDecision.BackendDescription}");
        LogMessage?.Invoke($"GPU backend: {gpuDecision.BackendDescription}");

        _ollama = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _ollama.OutputDataReceived += (_, args) => ForwardOllamaLine(args.Data);
        _ollama.ErrorDataReceived += (_, args) => ForwardOllamaLine(args.Data);
        _ollama.Exited += (_, _) =>
        {
            LogMessage?.Invoke("Ollama exited.");
            CurrentPort = null;
            ProcessExited?.Invoke();
        };

        _ollama.Start();
        _ollama.BeginOutputReadLine();
        _ollama.BeginErrorReadLine();
        CurrentPort = port;
        _logger?.Info($"Started ollama on port {port}");

        return new OllamaStartResult(true);
    }

    /// <summary>
    /// Routes one line of Ollama output to the right sink. llama-server emits roughly seven
    /// lines per embedding/generation request (slot scheduling, cache state, request done). On a
    /// large ingest that is thousands of lines a second — forwarded verbatim to the on-screen log
    /// it saturated the WPF dispatcher and froze the runner during the F18-guide ingest (#68).
    /// The per-request churn now goes to the file log only (DEBUG); meaningful lifecycle lines —
    /// and anything that looks like a warning or error — still surface in the UI via
    /// <see cref="LogMessage"/>, which also persists them to the file.
    /// </summary>
    private void ForwardOllamaLine(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return;

        if (IsVerboseServerLine(data))
        {
            _logger?.Debug(data);
            return;
        }

        LogMessage?.Invoke(data);
    }

    /// <summary>
    /// True for high-frequency llama-server per-request scheduler/server chatter that is noise to
    /// a user. Conservative: any line mentioning an error/warning/failure is never suppressed.
    /// </summary>
    private static bool IsVerboseServerLine(string line)
    {
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("warn", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("fail", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return line.StartsWith("slot ", StringComparison.Ordinal)
            || line.StartsWith("srv ", StringComparison.Ordinal)
            || line.Contains("update_slots", StringComparison.Ordinal)
            || line.Contains("log_server_r", StringComparison.Ordinal)
            || line.Contains("launch_slot_", StringComparison.Ordinal)
            || line.Contains("get_availabl", StringComparison.Ordinal)
            || line.Contains("kv cache", StringComparison.OrdinalIgnoreCase);
    }

    public void Stop()
    {
        if (_ollama is { HasExited: false })
        {
            _ollama.Kill(entireProcessTree: true);
            _ollama.Dispose();
            _ollama = null;
            CurrentPort = null;
            _logger?.Info("Stopped ollama");
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private static int ResolvePort(int preferred)
    {
        for (var port = preferred; port < preferred + 20; port++)
        {
            if (FreeAiSsd.Shared.NetUtils.IsPortFree(port)) return port;
        }

        throw new InvalidOperationException("No free ports in range.");
    }

    /// <summary>
    /// Kills any ollama / llama-server process whose executable lives under this SSD's
    /// staged tools folder — orphans left behind by a runner that crashed without
    /// running <see cref="Stop"/>. Scoped by path so a system-wide ollama (or one from
    /// another drive) is never touched. Best-effort: inspection can throw for processes
    /// we can't open, in which case we skip them.
    /// </summary>
    private void KillStaleInstances(string ollamaExe)
    {
        var toolsDir = Path.GetDirectoryName(ollamaExe);
        if (toolsDir is null) return;

        foreach (var name in new[] { "ollama", "llama-server" })
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    var path = proc.MainModule?.FileName;
                    if (path is not null &&
                        path.StartsWith(toolsDir, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger?.Info($"Reaping stale {name} process {proc.Id} from a prior session.");
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(3000);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Warn($"Could not inspect/reap {name} process {proc.Id}: {ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
    }
}
