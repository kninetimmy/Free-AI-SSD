using System.Diagnostics;
using System.Text.Json;
using FreeAiSsd.Shared;
using Xunit;

namespace FreeAiSsd.Tests;

/// <summary>
/// MAC17 Mac-side smoke. Mirrors <see cref="MacRunnerHostSmokeTests"/>
/// for the prep sidecar:
///   - In-process tests via <c>HostRunner.RunAsync(stdin, stdout, ...)</c>
///     run on Windows CI directly (no published binary needed).
///   - The published-binary test runs only on macOS where the
///     <c>mac-prep-build</c> CI job stages the osx-arm64 publish output.
///
/// The prep sidecar is simpler than mac-runner-host — no HTTP surface, no
/// long-running ASP.NET host, just stdin commands → stdout results.
/// </summary>
public sealed class MacPrepHostSmokeTests
{
    [Fact]
    public async Task HostRunner_HandshakeWithoutSsdRoot_FailsWithExitCode2()
    {
        var handshake = JsonSerializer.Serialize(new { ollamaHost = "http://127.0.0.1:11434" });

        using var stdin = new StringReader(handshake + Environment.NewLine);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await FreeAiSsd.MacPrepHost.HostRunner.RunAsync(
            stdin, stdout, stderr, new[] { "--test-mode" });

        Assert.Equal(2, exitCode);
        Assert.Contains("ssdRoot", stderr.ToString());
    }

    [Fact]
    public async Task HostRunner_TestMode_HandshakeReadinessShutdown_ExitsClean()
    {
        using var workdir = new TempDir("freeai-mac17-prep-smoke-");
        Directory.CreateDirectory(Path.Combine(workdir.Path, "logs"));
        Directory.CreateDirectory(Path.Combine(workdir.Path, "config"));

        var handshake = JsonSerializer.Serialize(new
        {
            ssdRoot = workdir.Path,
            ollamaHost = "http://127.0.0.1:11434",
        });

        // stdin sequence: handshake → readiness → shutdown.
        var inputBuilder = new System.Text.StringBuilder();
        inputBuilder.AppendLine(handshake);
        inputBuilder.AppendLine("readiness");
        inputBuilder.AppendLine("shutdown");

        using var stdin = new StringReader(inputBuilder.ToString());
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await FreeAiSsd.MacPrepHost.HostRunner.RunAsync(
            stdin, stdout, stderr, new[] { "--test-mode" });

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Contains("ready", output);
        Assert.Contains("result: readiness", output);
        Assert.Contains("\"testMode\":true", output);
    }

    [Fact]
    public async Task HostRunner_StdinEof_TreatedAsShutdown()
    {
        using var workdir = new TempDir("freeai-mac17-prep-eof-");
        Directory.CreateDirectory(Path.Combine(workdir.Path, "logs"));
        Directory.CreateDirectory(Path.Combine(workdir.Path, "config"));

        var handshake = JsonSerializer.Serialize(new { ssdRoot = workdir.Path });

        // Hand the handshake then immediately EOF — the host must shut down
        // gracefully so an orphaned sidecar can never outlive its parent.
        using var stdin = new StringReader(handshake + Environment.NewLine);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await FreeAiSsd.MacPrepHost.HostRunner.RunAsync(
            stdin, stdout, stderr, new[] { "--test-mode" });

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task HostRunner_UnknownCommand_LogsToStderrButContinues()
    {
        using var workdir = new TempDir("freeai-mac17-prep-unknown-");
        Directory.CreateDirectory(Path.Combine(workdir.Path, "logs"));
        Directory.CreateDirectory(Path.Combine(workdir.Path, "config"));

        var handshake = JsonSerializer.Serialize(new { ssdRoot = workdir.Path });
        var inputBuilder = new System.Text.StringBuilder();
        inputBuilder.AppendLine(handshake);
        inputBuilder.AppendLine("not-a-real-command");
        inputBuilder.AppendLine("readiness");
        inputBuilder.AppendLine("shutdown");

        using var stdin = new StringReader(inputBuilder.ToString());
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await FreeAiSsd.MacPrepHost.HostRunner.RunAsync(
            stdin, stdout, stderr, new[] { "--test-mode" });

        // Unknown commands are non-fatal — stderr message + loop continues
        // to the next valid command. This mirrors mac-runner-host's behavior
        // and lets a future protocol extension (Swift sending newer commands
        // to an older sidecar) degrade gracefully.
        Assert.Equal(0, exitCode);
        Assert.Contains("Unknown command", stderr.ToString());
        Assert.Contains("result: readiness", stdout.ToString());
    }

    [Fact]
    public async Task HostRunner_CancelPullWithNoActivePull_ReturnsOkAndContinues()
    {
        // MAC31: cancel-pull is idempotent. Sending it when no pull is in
        // flight emits result:cancel-pull {ok:true} and the loop carries
        // on so a defensive "always cancel before exiting" UI policy
        // doesn't crash the sidecar.
        using var workdir = new TempDir("freeai-mac31-cancel-noop-");
        Directory.CreateDirectory(Path.Combine(workdir.Path, "logs"));
        Directory.CreateDirectory(Path.Combine(workdir.Path, "config"));

        var handshake = JsonSerializer.Serialize(new { ssdRoot = workdir.Path });
        var inputBuilder = new System.Text.StringBuilder();
        inputBuilder.AppendLine(handshake);
        inputBuilder.AppendLine("cancel-pull");
        inputBuilder.AppendLine("readiness");
        inputBuilder.AppendLine("shutdown");

        using var stdin = new StringReader(inputBuilder.ToString());
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await FreeAiSsd.MacPrepHost.HostRunner.RunAsync(
            stdin, stdout, stderr, new[] { "--test-mode" });

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Contains("result: cancel-pull", output);
        Assert.Contains("\"ok\":true", output);
        // Loop must keep going after cancel-pull so the follow-up
        // command (readiness) still runs to completion.
        Assert.Contains("result: readiness", output);
    }

    [Fact]
    public async Task HostRunner_StageTesseractInTestMode_EmitsOkAndContinues()
    {
        // Task #91: the stage-tesseract command (Mac OCR staging) must be wired
        // into the dispatch loop and short-circuit cleanly in test-mode — same
        // shape as stage-piper — so the Swift PrepApp's OCR opt-in has a sidecar
        // command to call and a published host smoke can exercise it offline.
        using var workdir = new TempDir("freeai-mac91-stage-tesseract-");
        Directory.CreateDirectory(Path.Combine(workdir.Path, "logs"));
        Directory.CreateDirectory(Path.Combine(workdir.Path, "config"));

        var handshake = JsonSerializer.Serialize(new { ssdRoot = workdir.Path });
        var inputBuilder = new System.Text.StringBuilder();
        inputBuilder.AppendLine(handshake);
        inputBuilder.AppendLine("stage-tesseract");
        inputBuilder.AppendLine("readiness");
        inputBuilder.AppendLine("shutdown");

        using var stdin = new StringReader(inputBuilder.ToString());
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await FreeAiSsd.MacPrepHost.HostRunner.RunAsync(
            stdin, stdout, stderr, new[] { "--test-mode" });

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Contains("result: stage-tesseract", output);
        Assert.Contains("\"ok\":true", output);
        Assert.Contains("\"testMode\":true", output);
        // Loop continues to the follow-up command.
        Assert.Contains("result: readiness", output);
    }

    [Fact]
    public async Task HostRunner_PullModelInTestMode_EmitsProgressSeedThenResult()
    {
        // MAC31: even in test-mode (which short-circuits the real pull),
        // the result line for pull-model still appears in stdout. The
        // resume-seed `progress:` line is gated on a real pull (test
        // mode short-circuits before EmitProgress runs), so we only
        // pin the result-line wiring here. Real progress emission is
        // covered by MacPrepHostPullLifecycleTests via the FakeModelService.
        using var workdir = new TempDir("freeai-mac31-pull-testmode-");
        Directory.CreateDirectory(Path.Combine(workdir.Path, "logs"));
        Directory.CreateDirectory(Path.Combine(workdir.Path, "config"));

        var handshake = JsonSerializer.Serialize(new { ssdRoot = workdir.Path });
        var inputBuilder = new System.Text.StringBuilder();
        inputBuilder.AppendLine(handshake);
        inputBuilder.AppendLine("pull-model llama3.2:1b");
        // pull-model is detached at the loop layer (MAC31), so we need
        // the loop to stay alive long enough for the test-mode pull
        // task to run + write its result line. The next command
        // (readiness) runs sequentially behind it in test-mode (no
        // real cancellation race). shutdown then drains and exits.
        inputBuilder.AppendLine("readiness");
        inputBuilder.AppendLine("shutdown");

        using var stdin = new StringReader(inputBuilder.ToString());
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await FreeAiSsd.MacPrepHost.HostRunner.RunAsync(
            stdin, stdout, stderr, new[] { "--test-mode" });

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Contains("result: pull-model", output);
        Assert.Contains("\"testMode\":true", output);
        Assert.Contains("result: readiness", output);
    }

    [Fact]
    public async Task HostRunner_EnsureStructure_CreatesEverySsdLayoutDirectory()
    {
        // MAC17b drift-pin: when SsdLayout grows a new directory on the
        // C# side, this test fails until the expected[] list below is
        // updated — surfacing the change instead of silently shipping
        // Mac-prepped drives missing the new directory.
        using var workdir = new TempDir("freeai-mac17b-ensure-");
        // logs/ + config/ pre-created so SsdLogger and any other
        // construction-time IO succeeds; ensure-structure must still
        // create them idempotently and add everything else.
        Directory.CreateDirectory(Path.Combine(workdir.Path, "logs"));
        Directory.CreateDirectory(Path.Combine(workdir.Path, "config"));

        var handshake = JsonSerializer.Serialize(new { ssdRoot = workdir.Path });

        var inputBuilder = new System.Text.StringBuilder();
        inputBuilder.AppendLine(handshake);
        inputBuilder.AppendLine("ensure-structure");
        inputBuilder.AppendLine("shutdown");

        using var stdin = new StringReader(inputBuilder.ToString());
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await FreeAiSsd.MacPrepHost.HostRunner.RunAsync(
            stdin, stdout, stderr, new[] { "--test-mode" });

        Assert.Equal(0, exitCode);
        Assert.Contains("result: ensure-structure", stdout.ToString());

        // Every relative directory SsdLayout declares must exist on
        // disk. Keep this list explicit (don't reflect over SsdLayout's
        // constants) — the failure mode pinned here is "C# adds a dir,
        // Mac PrepApp silently doesn't ship it", which a reflection-based
        // test would silently track around.
        var expected = new[]
        {
            SsdLayout.Windows,
            SsdLayout.WindowsTools,
            SsdLayout.WindowsOllama,
            SsdLayout.WindowsPrereqs,
            SsdLayout.WindowsRunner,
            SsdLayout.Mac,
            SsdLayout.MacTools,
            SsdLayout.MacOllama,
            SsdLayout.Models,
            SsdLayout.Blobs,
            SsdLayout.WhisperModels,
            SsdLayout.Config,
            SsdLayout.Logs,
            SsdLayout.Cache,
            SsdLayout.Docs,
            SsdLayout.DocLibraries,
        };

        foreach (var rel in expected)
        {
            Assert.True(
                Directory.Exists(Path.Combine(workdir.Path, rel)),
                $"Expected SsdLayout directory '{rel}' to exist after ensure-structure.");
        }
    }

    [Fact]
    public async Task HostRunner_DiscoverCatalog_ReturnsBundledEntries()
    {
        // F2 protocol pin: discover-catalog returns the bundled
        // starter-models.json so the Mac picker has parity with Windows
        // immediately after staging — before the user has a chance to
        // click Refresh. The bundled catalog ships via prep-core's
        // <Content>+<EmbeddedResource> declarations, so this works whether
        // the test runs from a content-copied bin or via the embedded
        // fallback inside the test host.
        using var workdir = new TempDir("freeai-f2-discover-catalog-");
        Directory.CreateDirectory(Path.Combine(workdir.Path, "logs"));
        Directory.CreateDirectory(Path.Combine(workdir.Path, "config"));

        var handshake = JsonSerializer.Serialize(new { ssdRoot = workdir.Path });
        var inputBuilder = new System.Text.StringBuilder();
        inputBuilder.AppendLine(handshake);
        inputBuilder.AppendLine("discover-catalog");
        inputBuilder.AppendLine("shutdown");

        using var stdin = new StringReader(inputBuilder.ToString());
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await FreeAiSsd.MacPrepHost.HostRunner.RunAsync(
            stdin, stdout, stderr, new[] { "--test-mode" });

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Contains("result: discover-catalog", output);
        Assert.Contains("\"ok\":true", output);
        Assert.Contains("\"entries\":", output);

        // Spot-check a known bundled entry. starter-models.json ships
        // llama3.1:8b at the time of writing — if a future catalog edit
        // removes it, this drift-pin breaks loudly so the test gets
        // updated alongside the JSON.
        Assert.Contains("\"tag\":\"llama3.1:8b\"", output);
    }

    [Fact]
    public async Task HostRunner_RefreshCatalog_TestMode_EmitsSyntheticOkPayload()
    {
        // F2 protocol pin: refresh-catalog must surface as a clean result
        // line with the same key shape Swift will decode against. The
        // test-mode short-circuit avoids crossing the real network in CI;
        // failure-mode coverage lives in LiveModelCatalogServiceTests.
        using var workdir = new TempDir("freeai-f2-refresh-catalog-");
        Directory.CreateDirectory(Path.Combine(workdir.Path, "logs"));
        Directory.CreateDirectory(Path.Combine(workdir.Path, "config"));

        var handshake = JsonSerializer.Serialize(new { ssdRoot = workdir.Path });
        var inputBuilder = new System.Text.StringBuilder();
        inputBuilder.AppendLine(handshake);
        inputBuilder.AppendLine("refresh-catalog");
        inputBuilder.AppendLine("shutdown");

        using var stdin = new StringReader(inputBuilder.ToString());
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await FreeAiSsd.MacPrepHost.HostRunner.RunAsync(
            stdin, stdout, stderr, new[] { "--test-mode" });

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Contains("result: refresh-catalog", output);

        // Drift-pin the JSON keys Swift decodes against. If any of these
        // change without an accompanying Swift update, this fails.
        Assert.Contains("\"ok\":true", output);
        Assert.Contains("\"testMode\":true", output);
        Assert.Contains("\"fetchedAt\":", output);
        Assert.Contains("\"sourceUrl\":", output);
        Assert.Contains("\"entries\":", output);
    }

    [Fact]
    public async Task PublishedHost_TestMode_HandshakeReadinessShutdown()
    {
        if (!OperatingSystem.IsMacOS())
        {
            // Windows CI runs the .NET test suite — skip the Mac-binary smoke.
            return;
        }

        var binary = LocateHostBinary();
        if (binary is null)
        {
            // Mac CI publishes mac-prep-host before test; if a developer
            // runs `dotnet test` on Mac without first publishing, surface
            // a clear no-op rather than a confusing missing-file failure.
            return;
        }

        using var workdir = new TempDir("freeai-mac17-prep-bin-");
        Directory.CreateDirectory(Path.Combine(workdir.Path, "logs"));
        Directory.CreateDirectory(Path.Combine(workdir.Path, "config"));

        var handshake = JsonSerializer.Serialize(new
        {
            ssdRoot = workdir.Path,
            ollamaHost = "http://127.0.0.1:11434",
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

        Assert.True(proc.Start(), "mac-prep-host failed to start.");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.StandardInput.WriteLineAsync(handshake);
            await proc.StandardInput.FlushAsync();

            await WaitForLineAsync(stdoutLines, "ready", TimeSpan.FromSeconds(10));

            await proc.StandardInput.WriteLineAsync("readiness");
            await proc.StandardInput.FlushAsync();

            await WaitForLineAsync(stdoutLines, "result: readiness", TimeSpan.FromSeconds(10));

            await proc.StandardInput.WriteLineAsync("shutdown");
            await proc.StandardInput.FlushAsync();

            var exited = proc.WaitForExit(milliseconds: 5_000);
            Assert.True(exited, $"mac-prep-host did not exit within 5s.\nstderr:\n{string.Join("\n", stderrLines)}");
            Assert.Equal(0, proc.ExitCode);
        }
        catch
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    private static async Task WaitForLineAsync(List<string> stdoutLines, string startsWith, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            string[] snapshot;
            lock (stdoutLines) snapshot = stdoutLines.ToArray();
            foreach (var line in snapshot)
            {
                if (line.StartsWith(startsWith, StringComparison.Ordinal))
                {
                    return;
                }
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"mac-prep-host did not emit a line starting with '{startsWith}' within the timeout. stdout:\n" + string.Join("\n", stdoutLines));
    }

    private static string? LocateHostBinary()
    {
        var repoRoot = FindRepoRoot();
        if (repoRoot is null) return null;
        var candidates = new[]
        {
            Path.Combine(repoRoot, "mac-prep-host", "bin", "Release", "net8.0", "osx-arm64", "publish", "FreeAiSsd.MacPrepHost"),
            Path.Combine(repoRoot, "mac-prep-host", "bin", "Release", "net8.0", "publish", "FreeAiSsd.MacPrepHost"),
            Path.Combine(repoRoot, "mac-prep-host", "bin", "Release", "net8.0", "FreeAiSsd.MacPrepHost"),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FreeAiSsd.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
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
