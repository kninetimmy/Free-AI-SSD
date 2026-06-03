using FreeAiSsd.Shared;

namespace FreeAiSsd.PrepApp;

/// <summary>
/// Cheap, presence-only inspection of a previously-configured SSD to decide
/// whether the PrepApp should offer to resume an interrupted setup (#2). Never
/// decrypts config and never hashes model blobs — that depth stays in
/// <see cref="Services.ReadinessService"/>, which only runs once the user
/// reaches Finalize. Safe to call on any candidate drive (null / missing /
/// foreign), each maps to <see cref="SsdSetupCompletionResult.Complete"/>.
///
/// Detection order mirrors how a setup actually proceeds:
///   1. Not our drive (no config marker) → nothing to resume.
///   2. No usable models (none on disk, or a manifested model's blob is
///      missing → interrupted/partial pull) → resume at the Models step.
///   3. No platform runtime staged (neither the Windows runner nor the macOS
///      Runner.app) → resume at the Finalize step.
/// A drive that clears all three is treated as complete (no prompt).
/// </summary>
public static class SsdSetupCompletionProbe
{
    public static SsdSetupCompletionResult Inspect(string? ssdRoot)
    {
        if (string.IsNullOrWhiteSpace(ssdRoot) || !Directory.Exists(ssdRoot))
        {
            return SsdSetupCompletionResult.Complete;
        }

        // Foreign-data guard: only drives carrying our config marker are
        // candidates for a resume prompt (mirrors DriveConfigurationDetector).
        var config = DriveConfigurationDetector.Detect(ssdRoot);
        if (config.State == DriveConfigurationState.Unconfigured)
        {
            return SsdSetupCompletionResult.Complete;
        }

        var modelsRoot = Path.Combine(ssdRoot, SsdLayout.Models);
        IReadOnlyCollection<string> discovered;
        try
        {
            discovered = ModelOperations.DiscoverModelsOnDisk(modelsRoot);
        }
        catch
        {
            // Detection is best-effort; a faulted enumeration reads as "no
            // usable models" rather than crashing the prep launch path.
            discovered = Array.Empty<string>();
        }

        if (discovered.Count == 0)
        {
            return new SsdSetupCompletionResult(
                SsdSetupCompletionState.ModelsMissingOrIncomplete,
                "no AI models finished downloading to the drive");
        }

        foreach (var model in discovered)
        {
            string? blob;
            try
            {
                blob = ModelOperations.FindModelBlobForModel(modelsRoot, model);
            }
            catch
            {
                blob = null;
            }

            if (blob is null || !File.Exists(blob))
            {
                return new SsdSetupCompletionResult(
                    SsdSetupCompletionState.ModelsMissingOrIncomplete,
                    $"the model “{model}” didn’t finish downloading");
            }
        }

        // A drive is runnable if either platform's runner is staged. This keeps
        // a deliberate Windows-only or macOS-only prep from false-flagging.
        var windowsRunner = Path.Combine(ssdRoot, SsdLayout.WindowsRunner, "FreeAiSsd.Runner.exe");
        var macRunner = Path.Combine(ssdRoot, SsdLayout.MacRunner);
        var runtimeStaged = File.Exists(windowsRunner) || Directory.Exists(macRunner);
        if (!runtimeStaged)
        {
            return new SsdSetupCompletionResult(
                SsdSetupCompletionState.RuntimeNotStaged,
                "the Runner app wasn’t finished staging to the drive");
        }

        return SsdSetupCompletionResult.Complete;
    }
}
