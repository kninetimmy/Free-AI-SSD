using Microsoft.Win32;

namespace FreeAiSsd.Shared;

public sealed record DependencyCheckResult(bool IsSatisfied, IReadOnlyList<MissingDependency> MissingItems);

public sealed record MissingDependency(string Id, string DisplayName, bool RequiresAdmin);

public static class DependencyChecker
{
    public static DependencyCheckResult Check(string ssdRoot)
    {
        var missing = new List<MissingDependency>();
        if (!HasVcRuntimeX64())
        {
            missing.Add(new MissingDependency(
                PrereqCatalog.VcRedistX64Id,
                "Microsoft Visual C++ Redistributable (x64)",
                requiresAdmin: true));
        }

        return new DependencyCheckResult(missing.Count == 0, missing);
    }

    private static bool HasVcRuntimeX64()
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
}
