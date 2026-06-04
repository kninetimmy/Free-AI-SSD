using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace FreeAiSsd.Tests;

/// <summary>
/// MAC6 Mac-side smoke. Spawns the published <c>FreeAiSsd.MacRunnerHost</c>
/// binary with <c>--test-mode</c> (so a stub IChatService takes the place of
/// the real Ollama call), pipes a known handshake on stdin, and exercises
/// <c>/api/health</c> + <c>/api/chat</c> over the loopback API. Asserts the
/// host exits cleanly when stdin closes.
///
/// Returns early on:
/// - Non-macOS hosts (the published binary is osx-arm64; no point running on Windows CI).
/// - Missing published binary (the mac-runner-build CI job stages this — when
///   it isn't present we treat it as a no-op rather than a hard failure so the
///   test class stays buildable on Windows CI).
///
/// The Mac CI job runs <c>dotnet publish mac-runner-host -r osx-arm64 …</c>
/// before <c>dotnet test</c>, populating the expected publish dir.
/// </summary>
public sealed class MacRunnerHostSmokeTests
{
    /// <summary>
    /// Pins the #85 contract inside <see cref="RunnerLocalApiService.StartAsync"/>:
    /// <c>networkModeEnabled</c> now means "expose on the LAN", not "run the API".
    /// With it OFF (device-only) the sidecar still starts on loopback so the
    /// <c>/chat/</c> web UI works on this device with no key. This supersedes the
    /// pre-#85 MAC34a behavior where a false flag failed closed without a ready
    /// line (the Swift runner used to hardcode the flag true to dodge that gate).
    /// </summary>
    [Fact]
    public async Task HostRunner_WithLanExposureOff_StartsOnLoopback()
    {
        using var workdir = new TempDir("freeai-mac6-host-loopback-");
        Directory.CreateDirectory(Path.Combine(workdir.Path, "logs"));
        Directory.CreateDirectory(Path.Combine(workdir.Path, "config"));

        var handshake = JsonSerializer.Serialize(new
        {
            ssdRoot = workdir.Path,
            ollamaHost = "127.0.0.1:11434",
            config = new
            {
                networkModeEnabled = false,
                networkBindAddress = "127.0.0.1",
                networkPort = GetFreePort(),
                networkRequireApiKey = false,
                networkApiKey = string.Empty,
                networkAllowTts = false,
                networkAllowRemoteStt = false,
                networkAllowRemoteVoiceQuery = false,
                networkMaxAudioUploadMB = 10
            }
        });

        // stdin yields the handshake then EOF, which the command loop treats as
        // shutdown — so a successful start exits cleanly with code 0.
        using var stdin = new StringReader(handshake + Environment.NewLine);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await FreeAiSsd.MacRunnerHost.HostRunner.RunAsync(
            stdin,
            stdout,
            stderr,
            new[] { "--test-mode" });

        Assert.Equal(0, exitCode);
        Assert.Contains("ready:", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("127.0.0.1", stdout.ToString());
        Assert.DoesNotContain("did not start", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublishedHost_ServesHealthAndChat_WithStubChat()
    {
        if (!OperatingSystem.IsMacOS())
        {
            // Windows CI runs the .NET test suite — gracefully skip the Mac-only smoke.
            return;
        }

        var binary = LocateHostBinary();
        if (binary is null)
        {
            // Mac CI runs publish before test, but if a developer runs `dotnet test`
            // without first publishing mac-runner-host, surface a clear no-op rather
            // than a confusing missing-file failure.
            return;
        }

        using var workdir = new TempDir("freeai-mac6-host-smoke-");
        // Required SSD layout pieces so SsdLogger can construct.
        Directory.CreateDirectory(Path.Combine(workdir.Path, "logs"));
        Directory.CreateDirectory(Path.Combine(workdir.Path, "config"));

        var freePort = GetFreePort();
        var configJson = new
        {
            networkModeEnabled = true,
            networkBindAddress = "127.0.0.1",
            networkPort = freePort,
            networkRequireApiKey = false,
            networkApiKey = string.Empty,
            networkAllowTts = false,
            networkAllowRemoteStt = false,
            networkAllowRemoteVoiceQuery = false,
            networkMaxAudioUploadMB = 10,
            models = new[]
            {
                new { name = "phi3", status = "Installed" }
            }
        };

        var handshake = JsonSerializer.Serialize(new
        {
            ssdRoot = workdir.Path,
            ollamaHost = "127.0.0.1:11434",
            config = configJson
        });

        var psi = new ProcessStartInfo
        {
            FileName = binary!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--test-mode");

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdoutLines) stdoutLines.Add(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderrLines) stderrLines.Add(e.Data); };

        Assert.True(proc.Start(), "mac-runner-host failed to start.");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.StandardInput.WriteLineAsync(handshake);
            await proc.StandardInput.FlushAsync();

            var baseUrl = await WaitForReadyAsync(stdoutLines, TimeSpan.FromSeconds(15));

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var health = await http.GetAsync($"{baseUrl}/api/health");
            Assert.True(health.IsSuccessStatusCode, $"/api/health returned {health.StatusCode}");

            var chat = await http.PostAsJsonAsync($"{baseUrl}/api/chat", new { model = "phi3", prompt = "ping" });
            Assert.True(chat.IsSuccessStatusCode, $"/api/chat returned {chat.StatusCode}: {await chat.Content.ReadAsStringAsync()}");
            var chatJson = await chat.Content.ReadFromJsonAsync<JsonElement>();
            var responseText = chatJson.GetProperty("responseText").GetString() ?? string.Empty;
            Assert.Contains("test-mode", responseText);

            // RunnerCli compatibility: drive the same paths through RunnerApiClient
            // (re-used Windows code, via the test compile-include). This is the
            // "RunnerCli connects to a Mac-hosted Runner without code changes"
            // acceptance criterion.
            using (var apiClient = new FreeAiSsd.RunnerCli.RunnerApiClient(new Uri(baseUrl), apiKey: null))
            {
                var healthDto = await apiClient.GetHealthAsync();
                Assert.Equal("ok", healthDto.Status);

                var models = await apiClient.GetModelsAsync();
                Assert.Contains("phi3", models);

                var chatDto = await apiClient.ChatAsync("phi3", "ping");
                Assert.Contains("test-mode", chatDto.ResponseText);

                var streamEvents = new List<FreeAiSsd.RunnerCli.RunnerApiClient.StreamEvent>();
                await foreach (var evt in apiClient.ChatStreamAsync("phi3", "ping"))
                {
                    streamEvents.Add(evt);
                    if (evt.Type == "complete") break;
                }
                Assert.Contains(streamEvents, e => e.Type == "start");
                Assert.Contains(streamEvents, e => e.Type == "token");
                Assert.Contains(streamEvents, e => e.Type == "complete");
            }

            // Closing stdin tells the host to shutdown gracefully.
            proc.StandardInput.Close();

            var exited = proc.WaitForExit(milliseconds: 5_000);
            Assert.True(exited, $"mac-runner-host did not exit within 5s.\nstderr:\n{string.Join("\n", stderrLines)}");
            Assert.Equal(0, proc.ExitCode);
        }
        catch
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    private static async Task<string> WaitForReadyAsync(List<string> stdoutLines, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            string[] snapshot;
            lock (stdoutLines) snapshot = stdoutLines.ToArray();
            foreach (var line in snapshot)
            {
                if (line.StartsWith("ready: ", StringComparison.Ordinal))
                {
                    return line.Substring("ready: ".Length).Trim();
                }
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("mac-runner-host did not emit a 'ready: <baseUrl>' line within the timeout. stdout:\n" + string.Join("\n", stdoutLines));
    }

    private static string? LocateHostBinary()
    {
        var repoRoot = FindRepoRoot();
        if (repoRoot is null) return null;
        var candidates = new[]
        {
            Path.Combine(repoRoot, "mac-runner-host", "bin", "Release", "net8.0", "osx-arm64", "publish", "FreeAiSsd.MacRunnerHost"),
            Path.Combine(repoRoot, "mac-runner-host", "bin", "Release", "net8.0", "publish", "FreeAiSsd.MacRunnerHost"),
            Path.Combine(repoRoot, "mac-runner-host", "bin", "Release", "net8.0", "FreeAiSsd.MacRunnerHost"),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                return c;
            }
        }
        return null;
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FreeAiSsd.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
