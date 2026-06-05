using System.Text.Json;

namespace FreeAiSsd.Shared.Models;

public sealed class CompanionConfig
{
    public string HostAddress { get; set; } = string.Empty;
    public int HostPort { get; set; } = 41555;
    public string ApiKey { get; set; } = string.Empty;
    public string PttBinding { get; set; } = string.Empty;
    public string? InputDeviceName { get; set; }
    public string? OutputDeviceName { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public bool PttActivationSoundEnabled { get; set; } = true;
    public bool PttOverlayEnabled { get; set; } = true;
    public bool ServerRequiresApiKey { get; set; } = true;
    public int SchemaVersion { get; set; } = 1;

    public static CompanionConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new CompanionConfig();
        }

        var config = JsonSerializer.Deserialize<CompanionConfig>(File.ReadAllText(path), JsonOptions())
            ?? throw new InvalidOperationException("Companion config is invalid JSON.");

        if (config.SchemaVersion != 1)
        {
            throw new InvalidOperationException("Unsupported companion config schemaVersion. Expected 1.");
        }

        return config;
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, JsonOptions());
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        File.WriteAllText(path, json);
    }

    // HostAddress is intentionally NOT required: the companion auto-discovers the
    // Runner on the LAN (RunnerBeaconListener) and fills the address at runtime, so
    // a blank host is "discover it" rather than "needs setup". A bad port is still
    // a misconfiguration worth flagging.
    public bool IsComplete()
        => HostPort > 0
           && HostPort <= 65535
           && !string.IsNullOrWhiteSpace(PttBinding)
           && (!ServerRequiresApiKey || !string.IsNullOrWhiteSpace(ApiKey));

    // codex
    internal static string ResolveApiKeyForSave(string? existingApiKey, string? enteredApiKey, bool clearRequested)
    {
        var typedApiKey = enteredApiKey?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(typedApiKey))
        {
            return typedApiKey;
        }

        if (clearRequested)
        {
            return string.Empty;
        }

        return existingApiKey?.Trim() ?? string.Empty;
    }

    private static JsonSerializerOptions JsonOptions()
        => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
}
