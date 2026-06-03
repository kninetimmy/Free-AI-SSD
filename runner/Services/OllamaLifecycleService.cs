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
        _ollama.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data)) LogMessage?.Invoke(args.Data);
        };
        _ollama.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data)) LogMessage?.Invoke(args.Data);
        };
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
