using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace FreeAiSsd.Shared;

public sealed record DependencyCheckResult(bool IsSatisfied, IReadOnlyList<MissingDependency> MissingItems);

public sealed record MissingDependency(string Id, string DisplayName, bool RequiresAdmin, bool IsOptional = false);

public static class DependencyChecker
{
    public static DependencyCheckResult Check(string ssdRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new DependencyCheckResult(true, Array.Empty<MissingDependency>());
        }

        var missing = new List<MissingDependency>();

        var hasVcRuntime = HasVcRuntimeX64Windows() || HasVcRuntimeByOllamaProbe(ssdRoot);
        if (!hasVcRuntime)
        {
            missing.Add(new MissingDependency(PrereqCatalog.VcRedistX64Id, "Microsoft Visual C++ Redistributable (x64)", RequiresAdmin: true));
        }

        if (!IsDotnetDesktopOptional() && !HasDotnetDesktopRuntimeWindows())
        {
            missing.Add(new MissingDependency(PrereqCatalog.DotnetDesktop8X64Id, ".NET 8 Windows Desktop Runtime (x64)", RequiresAdmin: true));
        }

        return new DependencyCheckResult(missing.Count == 0, missing);
    }

    public static bool IsDotnetDesktopOptional() => true;

    [SupportedOSPlatform("windows")]
    private static bool HasVcRuntimeX64Windows()
    {
        var keys = new[]
        {
            @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64",
            @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64"
        };

        foreach (var keyPath in keys)
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key is null)
            {
                continue;
            }

            var installedValue = key.GetValue("Installed");
            if (installedValue is int installedInt && installedInt == 1)
            {
                return true;
            }

            var version = key.GetValue("Version")?.ToString();
            if (!string.IsNullOrWhiteSpace(version))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasVcRuntimeByOllamaProbe(string ssdRoot)
    {
        try
        {
            var ollamaPath = Path.Combine(ssdRoot, SsdLayout.Ollama, "ollama.exe");
            if (!File.Exists(ollamaPath))
            {
                return false;
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ollamaPath,
                Arguments = "--version",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            var stderr = process.StandardError.ReadToEnd();
            return !stderr.Contains("VCRUNTIME", StringComparison.OrdinalIgnoreCase)
                && !stderr.Contains("MSVCP", StringComparison.OrdinalIgnoreCase)
                && !stderr.Contains("api-ms-win-crt", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasDotnetDesktopRuntimeWindows()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--list-runtimes",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output.Split('\n').Any(line =>
                line.Contains("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase)
                && line.Contains(" 8.", StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }
}
