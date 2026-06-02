using FreeAiSsd.Shared;

namespace FreeAiSsd.Runner.Services;

public interface IModelManagementService
{
    event Action<string>? LogMessage;

    /// <summary>
    /// Returns the names of all models with <see cref="ModelInstallStatus.Installed"/> status.
    /// </summary>
    List<string> GetInstalledModelNames(PortableConfig config);

    /// <summary>
    /// Returns true if the configured embedding model (<see cref="PortableConfig.EmbeddingModelName"/>)
    /// is present in the installed-model set. Disk-truth based, so it is meaningful even before
    /// Ollama is running. Tag-aware: a bare name matches its <c>:latest</c> tag and vice versa.
    /// </summary>
    bool IsEmbeddingModelInstalled(PortableConfig config);

    /// <summary>
    /// Computes sizing warnings for installed models vs. current hardware.
    /// Returns human-readable warning strings (empty if no issues).
    /// </summary>
    List<string> GetModelSizingWarnings(PortableConfig config);

    /// <summary>
    /// Returns true if the user previously dismissed the sizing warning on this machine.
    /// </summary>
    bool IsSizingWarningDismissed(string ssdRoot);

    /// <summary>
    /// Persists the "don't show again" preference for sizing warnings.
    /// </summary>
    Task DismissSizingWarningAsync(string ssdRoot);

    /// <summary>
    /// Pulls the specified embedding model from the running Ollama instance.
    /// </summary>
    Task<bool> PullEmbeddingModelAsync(string host, string modelName);
}
