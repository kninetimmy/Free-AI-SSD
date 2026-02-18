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

            return Enum.TryParse<PrepTargets>(model.PrepTargets, out var parsed) && parsed != PrepTargets.None
                ? parsed
                : PrepTargets.Windows;
        }
        catch
        {
            return PrepTargets.Windows;
        }
    }

    public void Save(PrepTargets targets)
    {
        var safeTargets = targets == PrepTargets.None ? PrepTargets.Windows : targets;
        var model = new PrepAppSettings
        {
            PrepTargets = safeTargets.ToString()
        };

        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
    }

    private sealed class PrepAppSettings
    {
        [System.Text.Json.Serialization.JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = 1;

        [System.Text.Json.Serialization.JsonPropertyName("prepTargets")]
        public string PrepTargets { get; init; } = nameof(PrepTargets.Windows);
    }
}
