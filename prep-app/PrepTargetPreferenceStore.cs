using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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
            if (model is null || model.SchemaVersion != 2)
            {
                return PrepPreferenceSnapshot.Default;
            }

            var targets = Enum.TryParse<PrepTargets>(model.PrepTargetsValue, out var parsed) && parsed != PrepTargets.None
                ? parsed
                : PrepTargets.Windows;

            return new PrepPreferenceSnapshot(
                targets,
                model.InstallVrCompanion,
                model.CompanionHostAddress ?? string.Empty,
                model.CompanionHostPort <= 0 ? 41555 : model.CompanionHostPort);
        }
        catch
        {
            return PrepPreferenceSnapshot.Default;
        }
    }

    public void Save(PrepTargets targets)
    {
        var existing = LoadSettings();
        SaveSettings(new PrepPreferenceSnapshot(targets, existing.InstallVrCompanion, existing.CompanionHostAddress, existing.CompanionHostPort));
    }

    public void SaveSettings(PrepPreferenceSnapshot snapshot)
    {
        var safeTargets = snapshot.PrepTargets == PrepTargets.None ? PrepTargets.Windows : snapshot.PrepTargets;
        var model = new PrepAppSettings
        {
            PrepTargetsValue = safeTargets.ToString(),
            InstallVrCompanion = snapshot.InstallVrCompanion,
            CompanionHostAddress = snapshot.CompanionHostAddress,
            CompanionHostPort = snapshot.CompanionHostPort <= 0 ? 41555 : snapshot.CompanionHostPort
        };

        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
    }

    private sealed class PrepAppSettings
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = 2;

        [JsonPropertyName("prepTargets")]
        public string PrepTargetsValue { get; init; } = nameof(PrepTargets.Windows);

        [JsonPropertyName("installVrCompanion")]
        public bool InstallVrCompanion { get; init; }

        [JsonPropertyName("companionHostAddress")]
        public string? CompanionHostAddress { get; init; }

        [JsonPropertyName("companionHostPort")]
        public int CompanionHostPort { get; init; } = 41555;
    }
}

public readonly record struct PrepPreferenceSnapshot(PrepTargets PrepTargets, bool InstallVrCompanion, string CompanionHostAddress, int CompanionHostPort)
{
    public static PrepPreferenceSnapshot Default => new(PrepTargets.Windows, false, string.Empty, 41555);
}
