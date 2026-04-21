using System.Text.Json.Serialization;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Models;

namespace FreeAiSsd.PrepApp;

public sealed class PrepTargetPreferenceStore
{
    private readonly string _settingsPath;

    public PrepTargetPreferenceStore()
    {
        var settingsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeAiSsd");
        Directory.CreateDirectory(settingsRoot);
        _settingsPath = Path.Combine(settingsRoot, "prepapp-settings.json");
    }

    public PrepTargets Load() => LoadSettings().PrepTargets;

    public PrepPreferenceSnapshot LoadSettings()
    {
        if (!File.Exists(_settingsPath))
        {
            return PrepPreferenceSnapshot.Default;
        }

        try
        {
            var model = JsonSerializer.Deserialize<PrepAppSettings>(File.ReadAllText(_settingsPath));
            if (model is null || model.SchemaVersion is < 2 or > 3)
            {
                return PrepPreferenceSnapshot.Default;
            }

            var targets = Enum.TryParse<PrepTargets>(model.PrepTargetsValue, out var parsed) && parsed != PrepTargets.None
                ? parsed
                : PrepTargets.Windows;
            UserProfile? selectedProfile = Enum.TryParse<UserProfile>(model.SelectedProfileValue, out var parsedProfile)
                ? parsedProfile
                : null;

            return new PrepPreferenceSnapshot(
                targets,
                selectedProfile,
                model.InstallVrCompanion,
                model.CompanionHostAddress ?? string.Empty,
                model.CompanionHostPort <= 0 ? 41555 : model.CompanionHostPort,
                model.FtueCompleted);
        }
        catch
        {
            return PrepPreferenceSnapshot.Default;
        }
    }

    public void Save(PrepTargets targets)
    {
        var existing = LoadSettings();
        SaveSettings(new PrepPreferenceSnapshot(
            targets,
            existing.SelectedProfile,
            existing.InstallVrCompanion,
            existing.CompanionHostAddress,
            existing.CompanionHostPort,
            existing.FtueCompleted));
    }

    public void SaveSettings(PrepPreferenceSnapshot snapshot)
    {
        var safeTargets = snapshot.PrepTargets == PrepTargets.None ? PrepTargets.Windows : snapshot.PrepTargets;
        var model = new PrepAppSettings
        {
            PrepTargetsValue = safeTargets.ToString(),
            SelectedProfileValue = snapshot.SelectedProfile?.ToString(),
            InstallVrCompanion = snapshot.InstallVrCompanion,
            CompanionHostAddress = snapshot.CompanionHostAddress,
            CompanionHostPort = snapshot.CompanionHostPort <= 0 ? 41555 : snapshot.CompanionHostPort,
            FtueCompleted = snapshot.FtueCompleted
        };

        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
    }

    public void MarkFtueCompleted()
    {
        var existing = LoadSettings();
        if (existing.FtueCompleted) return;
        SaveSettings(new PrepPreferenceSnapshot(
            existing.PrepTargets,
            existing.SelectedProfile,
            existing.InstallVrCompanion,
            existing.CompanionHostAddress,
            existing.CompanionHostPort,
            true));
    }

    private sealed class PrepAppSettings
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = 3;

        [JsonPropertyName("prepTargets")]
        public string PrepTargetsValue { get; init; } = nameof(PrepTargets.Windows);

        [JsonPropertyName("selectedProfile")]
        public string? SelectedProfileValue { get; init; }

        [JsonPropertyName("installVrCompanion")]
        public bool InstallVrCompanion { get; init; }

        [JsonPropertyName("companionHostAddress")]
        public string? CompanionHostAddress { get; init; }

        [JsonPropertyName("companionHostPort")]
        public int CompanionHostPort { get; init; } = 41555;

        [JsonPropertyName("ftue_completed")]
        public bool FtueCompleted { get; init; }
    }
}

public readonly record struct PrepPreferenceSnapshot(
    PrepTargets PrepTargets,
    UserProfile? SelectedProfile,
    bool InstallVrCompanion,
    string CompanionHostAddress,
    int CompanionHostPort,
    bool FtueCompleted)
{
    public static PrepPreferenceSnapshot Default => new(PrepTargets.Windows, null, false, string.Empty, 41555, false);
}
