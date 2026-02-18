using System.Text.Json;

namespace FreeAiSsd.Shared;

public sealed class RunnerFirstRunState
{
    public bool DependencyPromptShown { get; set; }
    public bool SizingWarningDismissed { get; set; }
    public DateTime LastCheckedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EncryptionUnlockedAtUtc { get; set; }

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

    public async Task SaveAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
    }
}
