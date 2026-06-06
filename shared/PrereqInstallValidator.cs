using System.Diagnostics;
using FreeAiSsd.Shared.Helpers;

namespace FreeAiSsd.Shared;

/// <summary>
/// A validated and ready-to-execute prerequisite installer, including the
/// resolved file path, silent install arguments, and elevation requirements.
/// </summary>
public sealed record ValidatedPrereqInstall(
    PrereqDefinition Definition,
    PrereqManifestEntry ManifestEntry,
    string InstallerPath,
    string SilentArgs,
    bool RequiresAdmin);

/// <summary>
/// Validates bundled prerequisite installers before execution. Performs multi-layer
/// security checks: manifest consistency, file existence, SHA-256 hash verification,
/// path traversal protection, and Authenticode signature validation (on Windows).
/// This ensures only known-good, untampered installers are executed on target machines.
/// </summary>
public static class PrereqInstallValidator
{
    /// <summary>User-facing instruction for resolving prerequisite issues.</summary>
    public const string RefreshMessage = "Run PrepApp on an online machine and click Update Prereqs, then re-run Runner.";
    private const string HashMismatchMessage = "Installer hash mismatch. Recreate SSD or run 'Update prereqs' in PrepApp.";

    /// <summary>
    /// Builds a validated installation plan from a list of missing dependencies.
    /// For each missing dependency, performs the following checks in order:
    /// 1. Verifies the dependency exists in the known prerequisite catalog.
    /// 2. Checks the manifest has an entry for the dependency.
    /// 3. Verifies the manifest filename matches the catalog expectation.
    /// 4. Validates the manifest entry has a SHA-256 hash.
    /// 5. Ensures the installer file path is safe (no path traversal).
    /// 6. Verifies the installer file exists on disk.
    /// 7. Computes and compares the file's SHA-256 hash.
    /// 8. Validates the Authenticode signature (Windows only, for Microsoft installers).
    /// Any failure at any step adds an error and skips that dependency.
    /// </summary>
    /// <param name="ssdRoot">Root path of the portable SSD.</param>
    /// <param name="missing">Dependencies identified as missing by DependencyChecker.</param>
    /// <param name="manifest">The prereq manifest containing download metadata and hashes.</param>
    /// <param name="onLog">Optional callback for informational logging.</param>
    /// <param name="onWarn">Optional callback for non-fatal warnings.</param>
    /// <param name="errors">Output list of validation errors that blocked installation.</param>
    /// <returns>List of validated installers that passed all checks and are safe to execute.</returns>
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

        // Build lookup dictionaries for O(1) access during iteration.
        var catalogById = PrereqCatalog.Tier1.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var manifestById = manifest.Prerequisites
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var dep in missing)
        {
            // Step 1: Is this a known prerequisite in our catalog?
            if (!catalogById.TryGetValue(dep.Id, out var definition))
            {
                onWarn?.Invoke($"Ignoring non-catalog prerequisite from manifest/UI: {dep.Id}");
                continue;
            }

            // Step 2: Does the manifest have an entry for this dependency?
            if (!manifestById.TryGetValue(dep.Id, out var entry))
            {
                errors.Add($"Missing manifest entry for {definition.DisplayName}. {RefreshMessage}");
                continue;
            }

            // Step 3: Does the manifest filename match the catalog expectation?
            if (!string.Equals(entry.Filename, definition.TargetFileName, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Manifest filename mismatch for {definition.DisplayName}. Expected '{definition.TargetFileName}', found '{entry.Filename}'. {RefreshMessage}");
                continue;
            }

            // Step 4: Is the SHA-256 hash present in the manifest?
            if (string.IsNullOrWhiteSpace(entry.Sha256))
            {
                errors.Add($"{definition.DisplayName}: {HashMismatchMessage} {RefreshMessage}");
                continue;
            }

            // Step 5: Is the installer path safe (no directory traversal)?
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

            // Step 6: Does the installer file exist?
            if (!File.Exists(installerPath))
            {
                errors.Add($"Installer missing for {definition.DisplayName}. {RefreshMessage}");
                continue;
            }

            // Step 7: Does the file hash match the manifest?
            var actualSha = ComputeSha256(installerPath);
            if (!string.Equals(actualSha, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{definition.DisplayName}: {HashMismatchMessage} {RefreshMessage}");
                continue;
            }

            // Step 8: Does the Authenticode signature validate (Windows, Microsoft installers)?
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

    /// <summary>
    /// Validates the health of the entire prerequisite bundle on the SSD by checking
    /// that all Tier 1 prerequisites have: matching installer files, manifest entries
    /// with correct filenames, and valid SHA-256 hashes.
    /// </summary>
    /// <param name="prereqDir">Directory containing the bundled installer files.</param>
    /// <param name="manifest">The prereq manifest to validate against.</param>
    /// <returns>List of issues found; empty list means the bundle is healthy.</returns>
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

    /// <summary>
    /// Computes the SHA-256 hash of a file, returning a lowercase hex string.
    /// </summary>
    private static string ComputeSha256(string path) =>
        CryptoUtils.ComputeSha256Hex(path);

    /// <summary>
    /// Validates the Authenticode digital signature of installer executables.
    /// Only enforced for Microsoft-signed prerequisites (VC++ Redist, .NET Desktop Runtime).
    /// Uses PowerShell's Get-AuthenticodeSignature cmdlet to check signature status
    /// and signer identity. Falls back gracefully if PowerShell is unavailable.
    /// </summary>
    private static (bool Valid, string Message) ValidateSignature(string installerPath, PrereqDefinition definition, Action<string>? onWarn)
    {
        var expectedSigner = "Microsoft Corporation";

        // Only enforce signature checks on known Microsoft prerequisites.
        if (definition.Id != PrereqCatalog.VcRedistX64Id && definition.Id != PrereqCatalog.DotnetDesktop8X64Id)
        {
            return (true, "No signature policy configured");
        }

        try
        {
            // Use PowerShell to retrieve the Authenticode signature status and signer subject.
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // Pass the installer path out-of-band via an environment variable so it never
            // enters the -Command string — no manual quote-escaping, no shell-injection
            // surface (security invariant). -LiteralPath takes the value verbatim.
            psi.Environment["FREEAI_SIG_PATH"] = installerPath;
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(
                "$s = Get-AuthenticodeSignature -LiteralPath $env:FREEAI_SIG_PATH; " +
                "Write-Output ($s.Status.ToString()); Write-Output ($s.SignerCertificate.Subject)");
            using var ps = Process.Start(psi);

            if (ps is null)
            {
                onWarn?.Invoke($"Authenticode validation unavailable for {definition.Id}; falling back to hash-only validation.");
                return (true, "Authenticode unavailable");
            }

            // Capture output with a timeout to prevent hanging.
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

            // First line = status (Valid/NotSigned/etc), second line = signer certificate subject.
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

    /// <summary>
    /// Captures stdout and stderr from a running process with a timeout.
    /// Uses async data received events to collect output lines thread-safely.
    /// Kills the process if it exceeds the timeout.
    /// </summary>
    /// <param name="process">The running process to capture output from.</param>
    /// <param name="timeoutMs">Maximum time to wait for the process to exit.</param>
    /// <param name="output">Captured stdout lines.</param>
    /// <param name="error">Captured stderr as a joined string.</param>
    /// <returns>True if the process exited within the timeout; false if killed.</returns>
    internal static bool TryCaptureProcessOutput(Process process, int timeoutMs, out string[] output, out string error)
    {
        var outputLines = new List<string>();
        var errorLines = new List<string>();

        // Subscribe to async output events with thread-safe list access.
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
                // Best-effort kill; process may have already exited.
            }

            output = Array.Empty<string>();
            error = string.Empty;
            return false;
        }

        // Second WaitForExit() ensures async output buffers are fully flushed.
        process.WaitForExit();
        output = outputLines.ToArray();
        error = string.Join(Environment.NewLine, errorLines);
        return true;
    }
}
