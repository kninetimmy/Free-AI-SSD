using System.Text.Json;
using System.Text.Json.Serialization;
using FreeAiSsd.Shared.Io;

namespace FreeAiSsd.Shared;

/// <summary>
/// Whisper model sizes available for speech-to-text.
/// Larger models are more accurate but require more RAM and are slower.
/// </summary>
public enum WhisperModelSize
{
    Tiny,
    Base,
    Small,
    Medium
}

/// <summary>
/// Tracks the installation lifecycle of a model on the portable SSD.
/// </summary>
public enum ModelInstallStatus
{
    NotInstalled,
    Downloading,
    Installed,
    Failed
}

/// <summary>
/// Configuration entry for a single LLM model on the SSD, tracking its
/// installation status, integrity hash, file size, and last verification timestamp.
/// </summary>
public sealed class ModelConfigEntry
{
    public string Name { get; set; } = string.Empty;
    public ModelInstallStatus Status { get; set; } = ModelInstallStatus.NotInstalled;
    /// <summary>SHA-256 hash of the model's primary blob, used for integrity verification.</summary>
    public string? Sha256 { get; set; }
    /// <summary>Total size of the model in bytes, if known.</summary>
    public long? SizeBytes { get; set; }
    /// <summary>UTC timestamp of the last successful integrity verification.</summary>
    public DateTime? LastVerifiedUtc { get; set; }
}

/// <summary>
/// Root configuration file for the portable SSD, stored at config/portable-config.json.
/// Contains the Ollama server settings, list of installed models, encryption state,
/// and preparation metadata. Uses atomic file writes (write-to-temp then rename)
/// to prevent corruption during unexpected shutdowns.
/// </summary>
public sealed class PortableConfig
{
    /// <summary>Config schema version for forward compatibility.</summary>
    public string Version { get; set; } = "1.0";

    /// <summary>
    /// Active user profile. Null means the SSD predates PrepApp-owned profile setup
    /// or the user has not finalized a profile choice yet. PrepApp writes this during
    /// finalization and applies matching defaults via <see cref="ProfileDefaults.Apply"/>.
    /// </summary>
    public UserProfile? ActiveProfile { get; set; }
    /// <summary>Preferred TCP port for the local Ollama server (default: 11434).</summary>
    public int OllamaPort { get; set; } = 11434;
    /// <summary>Relative path from SSD root to the Ollama executable.</summary>
    public string OllamaRelativePath { get; set; } = @"windows\tools\ollama\ollama.exe";
    /// <summary>List of model entries with their status and integrity data.</summary>
    public List<ModelConfigEntry> Models { get; set; } = new();
    /// <summary>Preferred compute mode: "cpu", "cuda", or "rocm".</summary>
    public string PreferredCompute { get; set; } = "cpu";
    /// <summary>UTC timestamp when the SSD was initially prepared.</summary>
    public DateTime PreparedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Whether the config has been encrypted with a user password.</summary>
    public bool IsEncrypted { get; set; }
    /// <summary>Encryption algorithm identifier (e.g., "AES-256-GCM") when encrypted.</summary>
    public string? EncryptionScheme { get; set; }

    /// <summary>Active reference document library ID (or null for disabled RAG).</summary>
    public string? ActiveDocumentLibraryId { get; set; }
    /// <summary>Number of chunks to retrieve per query.</summary>
    public int RetrievalTopK { get; set; } = 5;
    /// <summary>Chunk size (characters) used during indexing.</summary>
    public int ChunkSize { get; set; } = 1200;
    /// <summary>Chunk overlap (characters) used during indexing.</summary>
    public int ChunkOverlap { get; set; } = 200;
    /// <summary>Embedding model name served by local Ollama.</summary>
    public string EmbeddingModelName { get; set; } = "nomic-embed-text";
    /// <summary>
    /// Minimum cosine similarity score (0.0–1.0) a retrieved chunk must reach to be
    /// included in the RAG context. Chunks below this threshold are discarded to avoid
    /// injecting irrelevant content into the LLM prompt. Default is 0.3.
    /// </summary>
    public double MinimumSimilarityThreshold { get; set; } = 0.3;
    /// <summary>
    /// Maximum number of chunk embeddings to request concurrently during document ingestion.
    /// Higher values may improve throughput but increase load on the local Ollama server.
    /// </summary>
    public int MaxEmbeddingConcurrency { get; set; } = 4;
    /// <summary>
    /// Maximum allowed document file size in megabytes for RAG ingestion.
    /// Files exceeding this limit are rejected before copying or parsing.
    /// </summary>
    public int MaxDocumentSizeMB { get; set; } = 50;

    /// <summary>
    /// When true, chat responses are streamed token-by-token from Ollama.
    /// Falls back to non-streaming if streaming fails. Default: true.
    /// </summary>
    public bool UseStreamingChat { get; set; } = true;

    // ── Network Mode (Runner LAN API) ────────────────────────────────────

    /// <summary>Enables Runner-hosted LAN API for remote clients on the local network.</summary>
    public bool NetworkModeEnabled { get; set; }
    /// <summary>
    /// IP address the Runner API binds to. Defaults to loopback ("127.0.0.1") so a
    /// freshly configured SSD does not accidentally expose the LAN API. Binding to
    /// "0.0.0.0" (all interfaces) is an explicit opt-in that requires a user-confirmed
    /// warning in the UI.
    /// </summary>
    public string NetworkBindAddress { get; set; } = "127.0.0.1";
    /// <summary>TCP port for the Runner LAN API.</summary>
    public int NetworkPort { get; set; } = 41555;
    /// <summary>Shared secret API key accepted via Bearer or X-API-Key header.</summary>
    public string NetworkApiKey { get; set; } = string.Empty;
    /// <summary>Require API key authentication for all non-health LAN API endpoints.</summary>
    public bool NetworkRequireApiKey { get; set; } = true;
    /// <summary>Allow remote clients to trigger host-side TTS playback and stop.</summary>
    public bool NetworkAllowTts { get; set; }
    /// <summary>Allow remote clients to upload audio for host-side Whisper transcription.</summary>
    public bool NetworkAllowRemoteStt { get; set; }
    /// <summary>Allow remote clients to run transcribe → optional chat → optional host TTS in one request.</summary>
    public bool NetworkAllowRemoteVoiceQuery { get; set; }
    /// <summary>
    /// Default behavior for LAN voice queries when request does not specify auto-send:
    /// true sends transcription directly to chat; false returns transcription only.
    /// </summary>
    public bool NetworkVoiceAutoSendToChat { get; set; } = true;
    /// <summary>Maximum audio upload size for LAN STT/voice endpoints in megabytes.</summary>
    public int NetworkMaxAudioUploadMB { get; set; } = 10;

    // ── Text-to-Speech ────────────────────────────────────────────────────

    /// <summary>Whether text-to-speech of AI responses is enabled.</summary>
    public bool TtsEnabled { get; set; }
    /// <summary>TTS engine to use: "system" (Windows SAPI) or "piper".</summary>
    public string TtsEngine { get; set; } = "system";
    /// <summary>Voice name for the selected TTS engine (null = engine default).</summary>
    public string? TtsVoiceName { get; set; }
    /// <summary>Speech rate. Range: -10 (slowest) to 10 (fastest). Default: 0.</summary>
    public int TtsRate { get; set; }
    /// <summary>Speech volume. Range: 0 (silent) to 100 (loudest). Default: 100.</summary>
    public int TtsVolume { get; set; } = 100;
    /// <summary>
    /// Audio output device name for TTS playback. Null means use the system default
    /// output device. Useful when the user wants AI voice on a specific device (e.g., VR headset).
    /// </summary>
    public string? TtsOutputDevice { get; set; }

    // ── Speech-to-Text ───────────────────────────────────────────────────

    /// <summary>Whisper model size for speech-to-text (tiny, base, small, medium).</summary>
    public WhisperModelSize WhisperModelSize { get; set; } = WhisperModelSize.Base;
    /// <summary>
    /// Device name of the preferred microphone for voice input.
    /// Null means use the system default recording device.
    /// </summary>
    public string? SelectedMicrophoneDevice { get; set; }
    /// <summary>
    /// When true, transcribed voice input is sent to the LLM automatically.
    /// When false, the text is placed in the prompt field for the user to review first.
    /// </summary>
    public bool AutoSendVoiceInput { get; set; } = true;

    // ── Push-to-Talk (HOTAS) ────────────────────────────────────────────

    /// <summary>Whether push-to-talk via HOTAS joystick button is enabled.</summary>
    public bool PttEnabled { get; set; }
    /// <summary>DirectInput device name for the PTT button (e.g., "X-56 Rhino Throttle").</summary>
    public string? PttDeviceName { get; set; }
    /// <summary>Zero-based button index on the joystick device.</summary>
    public int PttButtonIndex { get; set; }
    /// <summary>PTT mode: "push_to_talk" (hold to record) or "toggle" (press to start/stop).</summary>
    public string PttMode { get; set; } = "push_to_talk";
    /// <summary>Play a short beep when PTT is activated/deactivated.</summary>
    public bool PttActivationSoundEnabled { get; set; } = true;
    /// <summary>Show the always-on-top PTT status overlay. Disable for VR.</summary>
    public bool PttOverlayEnabled { get; set; } = true;
    /// <summary>Horizontal position of the PTT overlay window.</summary>
    public double PttOverlayX { get; set; } = 20;
    /// <summary>Vertical position of the PTT overlay window.</summary>
    public double PttOverlayY { get; set; } = 20;

    /// <summary>Standard relative path for the config file within the SSD structure.</summary>
    [JsonIgnore]
    public string ConfigRelativePath => @"config\\portable-config.json";

    /// <summary>
    /// Loads a config from disk with explicit validity reporting.
    /// Returns a default config with IsValid=false if the file is missing or corrupt.
    /// </summary>
    public static (PortableConfig Config, bool IsValid) LoadWithValidation(string path)
    {
        if (!File.Exists(path))
        {
            return (new PortableConfig(), false);
        }

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<PortableConfig>(json, JsonOptions());
            return (config ?? new PortableConfig(), config is not null);
        }
        catch
        {
            return (new PortableConfig(), false);
        }
    }

    /// <summary>
    /// Async version of LoadWithValidation for use in UI-bound contexts.
    /// </summary>
    public static async Task<(PortableConfig Config, bool IsValid)> LoadWithValidationAsync(string path)
    {
        if (!File.Exists(path))
        {
            return (new PortableConfig(), false);
        }

        try
        {
            var json = await File.ReadAllTextAsync(path);
            var config = JsonSerializer.Deserialize<PortableConfig>(json, JsonOptions());
            return (config ?? new PortableConfig(), config is not null);
        }
        catch
        {
            return (new PortableConfig(), false);
        }
    }

    /// <summary>
    /// Convenience loader that discards validation status and returns defaults on failure.
    /// </summary>
    public static PortableConfig Load(string path)
    {
        var (config, _) = LoadWithValidation(path);
        return config;
    }

    /// <summary>
    /// Async convenience loader that discards validation status.
    /// </summary>
    public static async Task<PortableConfig> LoadAsync(string path)
    {
        var (config, _) = await LoadWithValidationAsync(path);
        return config;
    }

    /// <summary>
    /// Synchronous save wrapper. Blocks the calling thread.
    /// </summary>
    public void Save(string path)
    {
        SaveAsync(path).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Error thrown by <see cref="SaveAsync"/> when Network Mode is enabled and requires
    /// an API key, but SSD config encryption is not effectively enabled. This prevents
    /// silently writing a plaintext portable-config.json that contains the API key shared
    /// secret while the Runner is configured to expose a network API.
    /// </summary>
    public const string NetworkModeEncryptionRequiredMessage =
        "Network Mode is enabled with Require API Key, but SSD config encryption is not enabled. " +
        "Enable SSD config encryption before saving, or disable Network Mode / Require API Key. " +
        "The API key is a shared secret and must not be written to disk unencrypted.";

    /// <summary>
    /// Persists the config to disk using an atomic write pattern:
    /// 1. Serialize to a temporary ".tmp" file.
    /// 2. Replace the original file atomically (or move if new).
    /// This prevents partial writes from corrupting the config.
    ///
    /// Fails closed when Network Mode + Require API Key are both on but the SSD config
    /// is not effectively encrypted — throws <see cref="InvalidOperationException"/>
    /// with <see cref="NetworkModeEncryptionRequiredMessage"/>. The SSD root is inferred
    /// as the parent directory of the config file's directory (e.g. config/portable-config.json
    /// ⇒ ssdRoot = ../).
    /// </summary>
    public async Task SaveAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Fail-closed guard: refuse to write a plaintext config that would expose a Network
        // Mode API key unless encryption is effectively on. We use the same write-guard
        // probe used elsewhere (IsEffectivelyEncryptedForWriteGuard) so the behavior is
        // consistent with PrepDriveWriteGuard's "encrypted" determination.
        if (NetworkModeEnabled && NetworkRequireApiKey)
        {
            var ssdRoot = TryInferSsdRoot(path);
            if (ssdRoot is not null && !SsdEncryption.IsEffectivelyEncryptedForWriteGuard(ssdRoot))
            {
                throw new InvalidOperationException(NetworkModeEncryptionRequiredMessage);
            }
        }

        var json = JsonSerializer.Serialize(this, JsonOptions());
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json);

        if (File.Exists(path))
        {
            FileOps.ReplaceWithRetry(tempPath, path, null);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }

    /// <summary>
    /// Infers the SSD root from a config file path of the form "{root}/config/portable-config.json".
    /// Returns null if the path does not follow the expected layout (e.g. a test passing a bare path).
    /// </summary>
    private static string? TryInferSsdRoot(string configPath)
    {
        var configDir = Path.GetDirectoryName(configPath);
        if (string.IsNullOrEmpty(configDir))
        {
            return null;
        }

        var parent = Path.GetDirectoryName(configDir);
        if (string.IsNullOrEmpty(parent))
        {
            return null;
        }

        // Only treat this as an SSD root if the immediate parent directory is named "config".
        // Otherwise callers writing to arbitrary paths (e.g. unit tests) should not be
        // forced through the encryption guard.
        var configDirName = new DirectoryInfo(configDir).Name;
        if (!string.Equals(configDirName, SsdLayout.Config, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return parent;
    }

    /// <summary>
    /// Standard JSON serialization options: indented, camelCase properties, string enums.
    /// </summary>
    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
}
