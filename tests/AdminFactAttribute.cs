using System.Runtime.Versioning;
using System.Security.Principal;
using Xunit;

namespace FreeAiSsd.Tests;

/// <summary>
/// xUnit [Fact] that auto-skips when the test process is not running
/// elevated on Windows. Used for integration tests that invoke
/// administrator-only APIs (diskpart, Format-Volume). Skip reason is
/// set at discovery time so CI runs on a non-admin agent report the
/// tests as skipped, not failed.
/// </summary>
public sealed class AdminFactAttribute : FactAttribute
{
    public AdminFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only integration test.";
            return;
        }

        if (!IsAdmin())
        {
            Skip = "Requires administrator privileges (diskpart + Format-Volume).";
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
