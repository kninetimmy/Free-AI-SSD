namespace FreeAiSsd.Shared;

/// <summary>
/// Distinct configuration states a candidate SSD can be in when PrepApp inspects it.
/// </summary>
public enum DriveConfigurationState
{
    /// <summary>
    /// No Free-AI-SSD config marker on disk. The drive may still contain
    /// foreign data (e.g., a user's own Ollama install) — that's deliberately
    /// not enough to claim it as ours. PrepApp's standard format-and-prep
    /// flow is the right path.
    /// </summary>
    Unconfigured,

    /// <summary>
    /// Our config marker (plaintext or encrypted) is present, but no model
    /// manifests have been written yet. Realistic case: user formatted and
    /// finalized but bailed before pulling a starter model. PrepApp should
    /// offer Manage-models, skipping format.
    /// </summary>
    ConfiguredEmpty,

    /// <summary>
    /// Our config marker is present AND at least one model manifest exists
    /// on disk. PrepApp should offer Manage-models / Start-over and skip
    /// the format flow on auto-select.
    /// </summary>
    FullyConfigured
}

/// <summary>
/// Snapshot of an SSD's configuration state from PrepApp's perspective.
/// All fields derive from file-presence probes — no config contents are read
/// or decrypted, so this is safe to call on any candidate drive without
/// touching encrypted state.
/// </summary>
public sealed record DriveConfigurationSnapshot(
    DriveConfigurationState State,
    bool HasOurConfig,
    bool IsConfigEncrypted,
    bool HasModels,
    int ModelManifestCount);

/// <summary>
/// Detects whether a candidate SSD has been previously configured by Free-AI-SSD.
///
/// Detection rules:
///   - <c>config/portable-config.json</c> OR <c>config/portable-config.encrypted.json</c>
///     present → drive carries our marker.
///   - Without our marker, model manifests on disk are ignored (foreign data
///     guard — a user's existing Ollama install on the same disk must not be
///     claimed as ours).
///   - With our marker, <c>models/manifests/**</c> presence promotes the
///     state from ConfiguredEmpty to FullyConfigured.
///
/// Detection never reads or decrypts config contents. The drive's encryption
/// status is inferred purely from filename presence (mirrors the posture of
/// <see cref="SsdEncryption.IsEffectivelyEncryptedForWriteGuard"/>).
/// </summary>
public static class DriveConfigurationDetector
{
    /// <summary>Plaintext portable-config filename (relative to config/).</summary>
    public const string PlaintextConfigFileName = "portable-config.json";

    /// <summary>
    /// Cap on manifest enumeration. Manifest counts inform UI copy ("3 models on
    /// this drive") but aren't load-bearing — exceeding the cap simply reports
    /// the cap, which is still enough to know HasModels is true. Internal so
    /// the test project can pin the cap without it being part of the public API.
    /// </summary>
    internal const int ManifestEnumerationCap = 256;

    /// <summary>Empty snapshot returned for null, missing, or unconfigured drives.</summary>
    public static DriveConfigurationSnapshot Empty { get; } =
        new(DriveConfigurationState.Unconfigured,
            HasOurConfig: false,
            IsConfigEncrypted: false,
            HasModels: false,
            ModelManifestCount: 0);

    /// <summary>
    /// Inspects <paramref name="ssdRoot"/> and returns a snapshot of its
    /// configuration state. Safe to call with null, empty, or a missing path —
    /// each maps to <see cref="Empty"/>.
    /// </summary>
    public static DriveConfigurationSnapshot Detect(string? ssdRoot)
    {
        if (string.IsNullOrWhiteSpace(ssdRoot))
        {
            return Empty;
        }

        if (!Directory.Exists(ssdRoot))
        {
            return Empty;
        }

        var configDir = Path.Combine(ssdRoot, SsdLayout.Config);
        var plaintextPath = Path.Combine(configDir, PlaintextConfigFileName);
        var encryptedPath = Path.Combine(configDir, SsdEncryption.EncryptedConfigFileName);

        var hasPlaintext = File.Exists(plaintextPath);
        var hasEncrypted = File.Exists(encryptedPath);
        var hasOurConfig = hasPlaintext || hasEncrypted;
        var isConfigEncrypted = hasEncrypted && !hasPlaintext;

        if (!hasOurConfig)
        {
            // Foreign-data guard: never claim a drive as ours based on model
            // manifests alone. The marker is our config file.
            return Empty;
        }

        var manifestsRoot = Path.Combine(ssdRoot, SsdLayout.Models, "manifests");
        var manifestCount = CountManifests(manifestsRoot);
        var hasModels = manifestCount > 0;

        var state = hasModels
            ? DriveConfigurationState.FullyConfigured
            : DriveConfigurationState.ConfiguredEmpty;

        return new DriveConfigurationSnapshot(
            state,
            HasOurConfig: true,
            IsConfigEncrypted: isConfigEncrypted,
            HasModels: hasModels,
            ModelManifestCount: manifestCount);
    }

    private static int CountManifests(string manifestsRoot)
    {
        try
        {
            if (!Directory.Exists(manifestsRoot))
            {
                return 0;
            }

            var count = 0;
            foreach (var _ in Directory.EnumerateFiles(
                         manifestsRoot, "*", SearchOption.AllDirectories))
            {
                count++;
                if (count >= ManifestEnumerationCap)
                {
                    break;
                }
            }

            return count;
        }
        catch
        {
            // UnauthorizedAccessException, IOException, etc. — detection is
            // best-effort and must not crash PrepApp boot.
            return 0;
        }
    }
}
