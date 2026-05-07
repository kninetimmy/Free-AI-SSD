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
