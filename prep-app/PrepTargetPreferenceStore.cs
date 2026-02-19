using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeAiSsd.PrepApp;

/// <summary>
/// Persists the user's prep target selection (Windows, macOS, or both)
/// to the local application data folder. This ensures the user's platform
/// preference is remembered between PrepApp sessions.
/// Stored at: %LOCALAPPDATA%/FreeAiSsd/prepapp-settings.json
/// </summary>
public sealed class PrepTargetPreferenceStore
{
    private readonly string _settingsPath;

    public PrepTargetPreferenceStore()
    {
        var settingsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeAiSsd");
        Directory.CreateDirectory(settingsRoot);
        _settingsPath = Path.Combine(settingsRoot, "prepapp-settings.json");
    }

    /// <summary>
    /// Loads the persisted prep target selection. Returns Windows as the default
    /// if the settings file is missing, corrupt, or has an unknown schema version.
    /// </summary>
    public PrepTargets Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return PrepTargets.Windows;
        }

        try
        {
            var model = JsonSerializer.Deserialize<PrepAppSettings>(File.ReadAllText(_settingsPath));
            if (model is null || model.SchemaVersion != 1)
            {
                return PrepTargets.Windows;
            }

            return Enum.TryParse<PrepTargets>(model.PrepTargetsValue, out var parsed) && parsed != PrepTargets.None
                ? parsed
                : PrepTargets.Windows;
        }
        catch
        {
            return PrepTargets.Windows;
        }
    }

    /// <summary>
    /// Saves the prep target selection to disk. Normalizes "None" to "Windows"
    /// to ensure at least one platform is always selected.
    /// </summary>
    public void Save(PrepTargets targets)
    {
        var safeTargets = targets == PrepTargets.None ? PrepTargets.Windows : targets;
        var model = new PrepAppSettings
        {
            PrepTargetsValue = safeTargets.ToString()
        };

        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
    }

    /// <summary>Internal JSON model for the settings file with schema versioning.</summary>
    private sealed class PrepAppSettings
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = 1;

        [JsonPropertyName("prepTargets")]
        public string PrepTargetsValue { get; init; } = nameof(FreeAiSsd.PrepApp.PrepTargets.Windows);
    }
}
