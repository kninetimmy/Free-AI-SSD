using FreeAiSsd.Shared;
using Whisper.net.Ggml;

namespace FreeAiSsd.Runner.Services;

/// <summary>
/// Manages downloading and locating Whisper GGML model files on the portable SSD.
/// Models are stored in the models/whisper directory alongside the LLM models.
/// </summary>
public static class WhisperModelManager
{
    private static readonly Dictionary<WhisperModelSize, GgmlType> ModelSizeMap = new()
    {
        [WhisperModelSize.Tiny] = GgmlType.Tiny,
        [WhisperModelSize.Base] = GgmlType.Base,
        [WhisperModelSize.Small] = GgmlType.Small,
        [WhisperModelSize.Medium] = GgmlType.Medium,
    };

    private static readonly Dictionary<WhisperModelSize, string> ModelFileNames = new()
    {
        [WhisperModelSize.Tiny] = "ggml-tiny.bin",
        [WhisperModelSize.Base] = "ggml-base.bin",
        [WhisperModelSize.Small] = "ggml-small.bin",
        [WhisperModelSize.Medium] = "ggml-medium.bin",
    };

    private static readonly Dictionary<WhisperModelSize, long> ApproximateSizeBytes = new()
    {
        [WhisperModelSize.Tiny] = 75_000_000,    // ~75 MB
        [WhisperModelSize.Base] = 142_000_000,    // ~142 MB
        [WhisperModelSize.Small] = 466_000_000,   // ~466 MB
        [WhisperModelSize.Medium] = 1_500_000_000, // ~1.5 GB
    };

    /// <summary>Returns the expected file path for a given model size on the SSD.</summary>
    public static string GetModelPath(string ssdRoot, WhisperModelSize size)
    {
        return Path.Combine(ssdRoot, SsdLayout.WhisperModels, ModelFileNames[size]);
    }

    /// <summary>Checks if a model file exists on the SSD.</summary>
    public static bool IsModelDownloaded(string ssdRoot, WhisperModelSize size)
    {
        return File.Exists(GetModelPath(ssdRoot, size));
    }

    /// <summary>Returns approximate download size in bytes for the given model.</summary>
    public static long GetApproximateSize(WhisperModelSize size)
    {
        return ApproximateSizeBytes[size];
    }

    /// <summary>
    /// Downloads the Whisper model if not already present on the SSD.
    /// Uses the Whisper.net built-in downloader from Hugging Face.
    /// </summary>
    public static async Task EnsureModelDownloadedAsync(
        string ssdRoot, WhisperModelSize size, Action<string>? onProgress = null)
    {
        var modelPath = GetModelPath(ssdRoot, size);

        if (File.Exists(modelPath))
        {
            onProgress?.Invoke($"Whisper model already exists: {ModelFileNames[size]}");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);

        var ggmlType = ModelSizeMap[size];
        var approxMB = ApproximateSizeBytes[size] / 1_000_000;
        onProgress?.Invoke($"Downloading Whisper {size} model (~{approxMB} MB)...");

        try
        {
            using var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(ggmlType);
            var tempPath = modelPath + ".tmp";
            using (var fileStream = File.Create(tempPath))
            {
                await modelStream.CopyToAsync(fileStream);
            }

            // Atomic move
            if (File.Exists(modelPath))
                File.Delete(modelPath);
            File.Move(tempPath, modelPath);

            onProgress?.Invoke($"Whisper model downloaded: {ModelFileNames[size]}");
        }
        catch (Exception)
        {
            // Clean up partial download
            var tempPath = modelPath + ".tmp";
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
            throw;
        }
    }

    /// <summary>
    /// Returns info about all available model sizes and whether they're downloaded.
    /// </summary>
    public static IReadOnlyList<WhisperModelInfo> GetAvailableModels(string ssdRoot)
    {
        return Enum.GetValues<WhisperModelSize>()
            .Select(size => new WhisperModelInfo
            {
                Size = size,
                FileName = ModelFileNames[size],
                IsDownloaded = IsModelDownloaded(ssdRoot, size),
                ApproximateSizeBytes = ApproximateSizeBytes[size],
            })
            .ToList();
    }
}

public sealed class WhisperModelInfo
{
    public WhisperModelSize Size { get; init; }
    public string FileName { get; init; } = string.Empty;
    public bool IsDownloaded { get; init; }
    public long ApproximateSizeBytes { get; init; }
}
