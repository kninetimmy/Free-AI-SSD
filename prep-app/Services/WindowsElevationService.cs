using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.PrepApp.Services;

public sealed class WindowsElevationService : IElevationService
{
    // Win32 ERROR_CANCELLED — user dismissed the UAC consent dialog.
    private const int UacCanceledErrorCode = 1223;

    public bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public bool TryRelaunchElevated(IEnumerable<string>? forwardArgs = null)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new InvalidOperationException("Cannot determine current executable path for elevated relaunch.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };

        // Forward args via ArgumentList (never string concat) so labels
        // containing spaces/quotes can't be reinterpreted by the shell.
        // ArgumentList is respected even with UseShellExecute=true on
        // modern .NET — each entry becomes one quoted CreateProcess token.
        if (forwardArgs is not null)
        {
            foreach (var arg in forwardArgs)
            {
                if (arg is not null) startInfo.ArgumentList.Add(arg);
            }
        }

        try
        {
            Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == UacCanceledErrorCode)
        {
            return false;
        }

        // New elevated instance has launched — tear down this non-elevated
        // one so the user doesn't end up with two windows.
        Application.Current?.Shutdown();
        return true;
    }
}
