using System.Text.Json;

namespace FreeAiSsd.Shared;

/// <summary>
/// Persisted state for the Runner app's first-run experience.
/// Tracks whether the user has seen the dependency install prompt,
/// dismissed model sizing warnings, and when the drive was last unlocked.
/// Stored at config/runner-first-run.json on the SSD.
/// </summary>
public sealed class RunnerFirstRunState
{
    /// <summary>Whether the dependency installation dialog has been shown at least once.</summary>
    public bool DependencyPromptShown { get; set; }
    /// <summary>Whether the user chose "Don't show again" for model sizing warnings.</summary>
    public bool SizingWarningDismissed { get; set; }
    /// <summary>UTC timestamp of the last dependency/configuration check.</summary>
    public DateTime LastCheckedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>UTC timestamp of the last successful drive unlock (for encrypted SSDs).</summary>
    public DateTime? EncryptionUnlockedAtUtc { get; set; }
    /// <summary>Whether the user has completed (or skipped) the first-time guided tour.</summary>
    public bool FtueCompleted { get; set; }

    /// <summary>
    /// Loads the first-run state from disk. Returns defaults if the file
    /// is missing or corrupt, ensuring the Runner always starts cleanly.
    /// </summary>
    public static RunnerFirstRunState Load(string path)
    {
        if (!File.Exists(path))
        {
            return new RunnerFirstRunState();
        }

        try
        {
            return JsonSerializer.Deserialize<RunnerFirstRunState>(File.ReadAllText(path)) ?? new RunnerFirstRunState();
        }
        catch
        {
            return new RunnerFirstRunState();
        }
    }

    /// <summary>
    /// Saves the first-run state to disk, creating parent directories if needed.
    /// </summary>
    public async Task SaveAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
    }
}
