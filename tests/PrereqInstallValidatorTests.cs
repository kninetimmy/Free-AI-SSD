using System.Diagnostics;
using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

public sealed class PrereqInstallValidatorTests
{
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
