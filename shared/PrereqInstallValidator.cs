using System.Diagnostics;
using System.Security.Cryptography;

namespace FreeAiSsd.Shared;

public sealed record ValidatedPrereqInstall(
    PrereqDefinition Definition,
    PrereqManifestEntry ManifestEntry,
    string InstallerPath,
    string SilentArgs,
    bool RequiresAdmin);

public static class PrereqInstallValidator
{
    public const string RefreshMessage = "Run PrepApp on an online machine and click Update Prereqs, then re-run Runner.";
    private const string HashMismatchMessage = "Installer hash mismatch. Recreate SSD or run 'Update prereqs' in PrepApp.";

    public static List<ValidatedPrereqInstall> BuildValidatedInstallPlan(
        string ssdRoot,
        IEnumerable<MissingDependency> missing,
        PrereqManifest manifest,
        Action<string>? onLog,
        Action<string>? onWarn,
        out List<string> errors)
    {
        errors = new List<string>();
        var plan = new List<ValidatedPrereqInstall>();
        var catalogById = PrereqCatalog.Tier1.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var manifestById = manifest.Prerequisites
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var dep in missing)
        {
            if (!catalogById.TryGetValue(dep.Id, out var definition))
            {
                onWarn?.Invoke($"Ignoring non-catalog prerequisite from manifest/UI: {dep.Id}");
                continue;
            }

            if (!manifestById.TryGetValue(dep.Id, out var entry))
            {
                errors.Add($"Missing manifest entry for {definition.DisplayName}. {RefreshMessage}");
                continue;
            }

            if (!string.Equals(entry.Filename, definition.TargetFileName, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Manifest filename mismatch for {definition.DisplayName}. Expected '{definition.TargetFileName}', found '{entry.Filename}'. {RefreshMessage}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Sha256))
            {
                errors.Add($"{definition.DisplayName}: {HashMismatchMessage} {RefreshMessage}");
                continue;
            }

            string installerPath;
            try
            {
                var prereqRoot = Path.Combine(ssdRoot, SsdLayout.Prereqs);
                installerPath = PathGuards.EnsureUnderRoot(prereqRoot, Path.Combine(prereqRoot, definition.TargetFileName));
            }
            catch (Exception ex)
            {
                errors.Add($"Unsafe installer path for {definition.DisplayName}: {ex.Message}. {RefreshMessage}");
                continue;
            }

            if (!File.Exists(installerPath))
            {
                errors.Add($"Installer missing for {definition.DisplayName}. {RefreshMessage}");
                continue;
            }

            var actualSha = ComputeSha256(installerPath);
            if (!string.Equals(actualSha, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{definition.DisplayName}: {HashMismatchMessage} {RefreshMessage}");
                continue;
            }

            var signatureResult = ValidateSignature(installerPath, definition, onWarn);
            if (!signatureResult.Valid)
            {
                errors.Add($"Signature validation failed for {definition.DisplayName}: {signatureResult.Message}. {RefreshMessage}");
                continue;
            }

            onLog?.Invoke($"Validated prerequisite package: {definition.Id}");
            plan.Add(new ValidatedPrereqInstall(definition, entry, installerPath, definition.SilentArgs, definition.RequiresAdmin));
        }

        return plan;
    }

    public static List<string> ValidateBundleHealth(string prereqDir, PrereqManifest manifest)
    {
        var issues = new List<string>();
        var manifestById = manifest.Prerequisites
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var definition in PrereqCatalog.Tier1)
        {
            var expectedPath = Path.Combine(prereqDir, definition.TargetFileName);
            if (!File.Exists(expectedPath))
            {
                issues.Add($"Missing installer file: {definition.TargetFileName}");
                continue;
            }

            if (!manifestById.TryGetValue(definition.Id, out var entry))
            {
                issues.Add($"Missing manifest entry: {definition.Id}");
                continue;
            }

            if (!string.Equals(entry.Filename, definition.TargetFileName, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add($"Manifest filename mismatch for {definition.Id}: expected {definition.TargetFileName}, found {entry.Filename}");
            }

            if (string.IsNullOrWhiteSpace(entry.Sha256))
            {
                issues.Add($"Missing SHA256 in manifest for {definition.Id}");
                continue;
            }

            var actualSha = ComputeSha256(expectedPath);
            if (!string.Equals(actualSha, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add($"SHA256 mismatch for {definition.Id}");
            }
        }

        return issues;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static (bool Valid, string Message) ValidateSignature(string installerPath, PrereqDefinition definition, Action<string>? onWarn)
    {
        var expectedSigner = "Microsoft Corporation";
        if (definition.Id != PrereqCatalog.VcRedistX64Id && definition.Id != PrereqCatalog.DotnetDesktop8X64Id)
        {
            return (true, "No signature policy configured");
        }

        try
        {
            using var ps = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"$s = Get-AuthenticodeSignature -FilePath '{installerPath.Replace("'", "''")}'; Write-Output ($s.Status.ToString()); Write-Output ($s.SignerCertificate.Subject)\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (ps is null)
            {
                onWarn?.Invoke($"Authenticode validation unavailable for {definition.Id}; falling back to hash-only validation.");
                return (true, "Authenticode unavailable");
            }

            if (!TryCaptureProcessOutput(ps, 10000, out var output, out var err))
            {
                onWarn?.Invoke($"Authenticode validation timed out for {definition.Id}; falling back to hash-only validation.");
                return (true, "Authenticode unavailable");
            }

            if (ps.ExitCode != 0 || output.Length == 0)
            {
                onWarn?.Invoke($"Authenticode validation could not run for {definition.Id}: {err}. Falling back to hash-only validation.");
                return (true, "Authenticode unavailable");
            }

            var status = output[0];
            var signer = output.Length > 1 ? output[1] : string.Empty;
            if (!string.Equals(status, "Valid", StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"Status={status}");
            }

            if (!signer.Contains(expectedSigner, StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"Unexpected signer '{signer}'");
            }

            return (true, "Valid");
        }
        catch (Exception ex)
        {
            onWarn?.Invoke($"Authenticode check failed for {definition.Id}: {ex.Message}. Falling back to hash-only validation.");
            return (true, "Authenticode unavailable");
        }
    }

    internal static bool TryCaptureProcessOutput(Process process, int timeoutMs, out string[] output, out string error)
    {
        var outputLines = new List<string>();
        var errorLines = new List<string>();

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                lock (outputLines)
                {
                    outputLines.Add(e.Data);
                }
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                lock (errorLines)
                {
                    errorLines.Add(e.Data);
                }
            }
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // best effort
            }

            output = Array.Empty<string>();
            error = string.Empty;
            return false;
        }

        process.WaitForExit();
        output = outputLines.ToArray();
        error = string.Join(Environment.NewLine, errorLines);
        return true;
    }
}
