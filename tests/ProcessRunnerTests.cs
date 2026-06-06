using System.Diagnostics;
using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

public class ProcessRunnerTests
{
    /// <summary>
    /// Real-process integration test for finding #120: when the caller cancels (or the
    /// operation times out), ProcessRunner must kill the child rather than letting
    /// Process.Dispose abandon it still running. A mock can't prove the OS process actually
    /// dies, so this launches a real long-running child, captures its PID, cancels, and
    /// asserts the child has exited. Without the kill-on-cancel fix the child sleeps out its
    /// full duration orphaned and this fails.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenCancelled_KillsChildProcess()
    {
        var (fileName, args) = LongRunningPidEcho();

        int? childPid = null;
        using var cts = new CancellationTokenSource();
        void OnOutput(string line)
        {
            if (childPid is null && int.TryParse(line.Trim(), out var pid))
            {
                childPid = pid;
                cts.Cancel();
            }
        }

        // Safety net so a child that never reports its PID can't hang the suite for 60s.
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ProcessRunner.RunAsync(fileName, args, Environment.CurrentDirectory, onOutput: OnOutput, ct: cts.Token));

        Assert.True(childPid.HasValue, "child process never reported its PID");
        Assert.True(
            WaitUntilExited(childPid.Value, TimeSpan.FromSeconds(15)),
            "ProcessRunner left the child running after cancellation — it was orphaned");
    }

    /// <summary>A command that prints its own PID on the first line, then blocks for 60s.</summary>
    private static (string FileName, IReadOnlyList<string> Args) LongRunningPidEcho()
        => OperatingSystem.IsWindows()
            ? ("powershell", new[] { "-NoProfile", "-Command", "[System.Diagnostics.Process]::GetCurrentProcess().Id; Start-Sleep -Seconds 60" })
            : ("/bin/bash", new[] { "-c", "echo $$; sleep 60" });

    private static bool WaitUntilExited(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited) return true;
            }
            catch (ArgumentException)
            {
                // No process with that id is running — it exited (was killed).
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }
}
