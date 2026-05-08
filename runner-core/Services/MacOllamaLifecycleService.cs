using System.Diagnostics;
using FreeAiSsd.Shared;

namespace FreeAiSsd.Runner.Services;

/// <summary>
/// macOS implementation of <see cref="IOllamaLifecycleService"/>. Mirrors the
/// Windows lifecycle service's security posture (trust attestation gate, loopback
/// bind, OLLAMA_MODELS env var, stdout/stderr capture, exit-event wiring) but
/// resolves the binary at <c>mac/tools/ollama/Ollama.app/Contents/Resources/ollama</c>
/// (the inner self-contained CLI server — see MAC26), validates the macOS
/// trust attestation, and lives in <c>runner-core/</c> so it builds plain
/// <c>net8.0</c> with no Windows-only packages.
/// </summary>
public sealed class MacOllamaLifecycleService : IOllamaLifecycleService
{
    private readonly SsdLogger? _logger;
    private Process? _ollama;

    public MacOllamaLifecycleService(SsdLogger? logger)
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
        var gate = OllamaPackageTrustPolicy.ValidateMacExecutionAttestation(
            ssdRoot, OllamaPackageTrustPolicy.DefaultMacPackage.Url);
        return (gate.IsTrusted, gate.Message);
    }

    public OllamaStartResult Start(PortableConfig config, string ssdRoot)
    {
        if (_ollama is { HasExited: false })
        {
            return new OllamaStartResult(false, "Ollama is already running.");
        }

        var ollamaBinary = ResolveBinaryPath(ssdRoot);
        if (!File.Exists(ollamaBinary))
        {
            return new OllamaStartResult(false, "ollama binary missing in staged mac/tools/ollama folder.");
        }

        int port;
        try
        {
            port = ResolvePort(config.OllamaPort);
        }
        catch (Exception ex)
        {
            return new OllamaStartResult(false, $"Unable to find a free port: {ex.Message}");
        }

        var startInfo = BuildStartInfo(ollamaBinary, ssdRoot, port);

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

    /// <summary>
    /// Builds the <see cref="ProcessStartInfo"/> for launching the Mac Ollama
    /// server. Exposed so tests can verify env vars and arguments without
    /// actually starting a process.
    /// </summary>
    public static ProcessStartInfo BuildStartInfo(string ollamaBinary, string ssdRoot, int port)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ollamaBinary,
            WorkingDirectory = Path.GetDirectoryName(ollamaBinary)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // Argument-array form is required by the security invariants — never
        // string-concatenate process arguments. ProcessStartInfo.ArgumentList
        // is the cross-platform equivalent of ProcessRunner.ArgumentList for
        // direct Process launches.
        startInfo.ArgumentList.Add("serve");

        startInfo.Environment["OLLAMA_MODELS"] = Path.Combine(ssdRoot, SsdLayout.Models);
        startInfo.Environment["OLLAMA_HOST"] = $"127.0.0.1:{port}";
        startInfo.Environment["OLLAMA_ORIGINS"] = "http://127.0.0.1,http://localhost";
        return startInfo;
    }

    /// <summary>
    /// Resolves the macOS Ollama binary path from the SSD root. MAC26: the
    /// upstream Mac distribution is a GUI app bundle; the self-contained CLI
    /// server is at <c>Ollama.app/Contents/Resources/ollama</c>. PrepApp's
    /// staging code now leaves the bundle intact and deletes the top-level
    /// LaunchServices shim, so this resolver points directly at the inner
    /// binary.
    /// </summary>
    public static string ResolveBinaryPath(string ssdRoot) =>
        Path.Combine(ssdRoot, SsdLayout.MacOllama, "Ollama.app", "Contents", "Resources", "ollama");

    private static int ResolvePort(int preferred)
    {
        for (var port = preferred; port < preferred + 20; port++)
        {
            if (NetUtils.IsPortFree(port)) return port;
        }

        throw new InvalidOperationException("No free ports in range.");
    }
}
