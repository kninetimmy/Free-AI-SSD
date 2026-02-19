using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace FreeAiSsd.Shared;

/// <summary>
/// Result of a system dependency check, indicating whether all required
/// prerequisites are present and listing any that are missing.
/// </summary>
public sealed record DependencyCheckResult(bool IsSatisfied, IReadOnlyList<MissingDependency> MissingItems);

/// <summary>
/// Represents a single missing system prerequisite, including metadata
/// about whether it needs elevated permissions and whether it is optional.
/// </summary>
public sealed record MissingDependency(string Id, string DisplayName, bool RequiresAdmin, bool IsOptional = false);

/// <summary>
/// Checks whether required system-level dependencies (VC++ Runtime, .NET Desktop Runtime)
/// are installed on the current machine. Only performs checks on Windows; other platforms
/// are assumed to satisfy all dependencies.
/// </summary>
public static class DependencyChecker
{
    /// <summary>
    /// Inspects the host system for required runtime dependencies.
    /// Uses registry probing and an Ollama binary probe as fallback for VC++ detection.
    /// </summary>
    /// <param name="ssdRoot">Root path of the portable SSD, used to locate the bundled Ollama binary for fallback probing.</param>
    /// <returns>A result indicating satisfaction status and any missing items.</returns>
    public static DependencyCheckResult Check(string ssdRoot)
    {
        // Non-Windows platforms are treated as fully satisfied since
        // the prerequisites are Windows-specific runtime libraries.
        if (!OperatingSystem.IsWindows())
        {
            return new DependencyCheckResult(true, Array.Empty<MissingDependency>());
        }

        var missing = new List<MissingDependency>();

        // Check for Visual C++ Redistributable via registry first,
        // then fall back to probing the Ollama binary for VC runtime errors.
        var hasVcRuntime = HasVcRuntimeX64Windows() || HasVcRuntimeByOllamaProbe(ssdRoot);
        if (!hasVcRuntime)
        {
            missing.Add(new MissingDependency(PrereqCatalog.VcRedistX64Id, "Microsoft Visual C++ Redistributable (x64)", RequiresAdmin: true));
        }

        // .NET Desktop Runtime is marked optional; only flag if the policy says otherwise.
        if (!IsDotnetDesktopOptional() && !HasDotnetDesktopRuntimeWindows())
        {
            missing.Add(new MissingDependency(PrereqCatalog.DotnetDesktop8X64Id, ".NET 8 Windows Desktop Runtime (x64)", RequiresAdmin: true));
        }

        return new DependencyCheckResult(missing.Count == 0, missing);
    }

    /// <summary>
    /// Policy flag: controls whether .NET Desktop Runtime absence is treated
    /// as a blocking issue. Currently always returns true (optional).
    /// </summary>
    public static bool IsDotnetDesktopOptional() => true;

    /// <summary>
    /// Checks the Windows registry for the presence of the Visual C++ x64
    /// Redistributable (VS 2015-2022 era, VC runtime 14.x).
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool HasVcRuntimeX64Windows()
    {
        // Both native and WOW6432Node keys are checked because the runtime
        // registration location varies by installation type.
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

    /// <summary>
    /// Fallback VC runtime detection: attempts to run ollama.exe --version
    /// and checks stderr for VC runtime-related error messages. If the binary
    /// runs without mentioning VCRUNTIME/MSVCP/api-ms-win-crt, the runtime is present.
    /// </summary>
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

            // Allow up to 5 seconds for the version check; kill if it hangs.
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            // If stderr mentions any VC runtime DLLs, the runtime is missing.
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

    /// <summary>
    /// Detects whether the .NET 8 Windows Desktop Runtime is installed
    /// by parsing the output of "dotnet --list-runtimes".
    /// </summary>
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

            // Look for a line containing "Microsoft.WindowsDesktop.App" with major version 8.
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
