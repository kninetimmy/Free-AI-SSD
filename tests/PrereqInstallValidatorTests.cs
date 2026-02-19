using System.Diagnostics;
using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

/// <summary>
/// Tests for PrereqInstallValidator — the process output capture utility.
/// Currently covers the timeout behavior of TryCaptureProcessOutput, which is
/// used to detect installed runtimes by running version-check commands with
/// strict time limits. A process that exceeds the timeout returns false with
/// empty output, preventing the Runner from hanging on unresponsive commands.
/// </summary>
public sealed class PrereqInstallValidatorTests
{
    /// <summary>
    /// Verifies that a long-running process (sleep 2s) times out with a 1ms limit
    /// and returns false with empty output. Uses bash on Linux and PowerShell on Windows.
    /// </summary>
    [Fact]
    public void TryCaptureProcessOutput_TimesOutAndReturnsFalse()
    {
        var startInfo = BuildSleepProcessStartInfo();
        using var process = Process.Start(startInfo);

        Assert.NotNull(process);
        var ok = PrereqInstallValidator.TryCaptureProcessOutput(process!, timeoutMs: 1, out var output, out var error);

        Assert.False(ok);
        Assert.Empty(output);
        Assert.Equal(string.Empty, error);
    }

    /// <summary>
    /// Creates a platform-appropriate process that sleeps for 2 seconds,
    /// used as the target for timeout testing.
    /// </summary>
    private static ProcessStartInfo BuildSleepProcessStartInfo()
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -Command Start-Sleep -Seconds 2",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        return new ProcessStartInfo
        {
            FileName = "bash",
            Arguments = "-lc \"sleep 2\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }
}
