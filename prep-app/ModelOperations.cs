using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace FreeAiSsd.PrepApp;

public sealed class ModelOperations
{
    public async Task<PullModelResult> PullModelAsync(string ollamaExe, string modelRoot, string modelTag, Action<string> onLog, CancellationToken ct)
    {
        var env = new Dictionary<string, string>
        {
            ["OLLAMA_MODELS"] = modelRoot
        };

        var exitCode = await RunProcessStreamingAsync(ollamaExe, BuildOllamaArgs("pull", modelTag), Path.GetDirectoryName(ollamaExe)!, env, onLog, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Failed to pull model {modelTag}. Exit code: {exitCode}");
        }

        var modelFile = FindModelBlobForModel(modelRoot, modelTag)
                        ?? throw new FileNotFoundException($"Unable to locate model blob for {modelTag} in {modelRoot}.");

        var sha256 = await ComputeSha256Async(modelFile, ct);
        var size = new FileInfo(modelFile).Length;
        return new PullModelResult(sha256, size);
    }

    public async Task<bool> VerifyModelAsync(string modelRoot, string modelTag, string expectedHash, Action<string> onLog, CancellationToken ct)
    {
        var modelBlob = FindModelBlobForModel(modelRoot, modelTag);
        if (modelBlob is null)
        {
            onLog($"Verify failed for {modelTag}: model blob not found.");
            return false;
        }

        var actualHash = await ComputeSha256Async(modelBlob, ct);
        var matches = string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        onLog(matches
            ? $"Verify passed for {modelTag}."
            : $"Verify failed for {modelTag}: expected {expectedHash}, got {actualHash}.");
        return matches;
    }

    public async Task DeleteModelAsync(string ollamaExe, string modelRoot, string modelTag, Action<string> onLog, CancellationToken ct)
    {
        var env = new Dictionary<string, string>
        {
            ["OLLAMA_MODELS"] = modelRoot
        };

        var exitCode = await RunProcessStreamingAsync(ollamaExe, BuildOllamaArgs("rm", modelTag), Path.GetDirectoryName(ollamaExe)!, env, onLog, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Failed to delete model {modelTag} from disk. Exit code: {exitCode}");
        }
    }

    public static IReadOnlyCollection<string> DiscoverModelsOnDisk(string modelRoot)
    {
        var manifestsPath = Path.Combine(modelRoot, "manifests");
        if (!Directory.Exists(manifestsPath))
        {
            return Array.Empty<string>();
        }

        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifestPath in Directory.EnumerateFiles(manifestsPath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(manifestsPath, manifestPath);
            var parts = relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var modelId = $"{parts[^2]}:{parts[^1]}";
            discovered.Add(modelId);
        }

        return discovered.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string? FindModelBlobForModel(string modelRoot, string model)
    {
        var manifestTag = model.Replace(':', '-');
        var manifestsPath = Path.Combine(modelRoot, "manifests", "registry.ollama.ai", "library");
        if (!Directory.Exists(manifestsPath))
        {
            return null;
        }

        var manifest = Directory.EnumerateFiles(manifestsPath, manifestTag, SearchOption.AllDirectories).FirstOrDefault();
        if (manifest is null)
        {
            return null;
        }

        var content = File.ReadAllText(manifest);
        if (!TrySelectModelLayerDigest(content, out var normalizedDigest))
        {
            return null;
        }

        var blob = Path.Combine(modelRoot, "blobs", normalizedDigest.Replace(':', '-'));
        return File.Exists(blob) ? blob : null;
    }

    internal static IReadOnlyList<string> BuildOllamaArgs(string command, string modelTag)
        => new[] { command, modelTag };

    internal static bool TrySelectModelLayerDigest(string manifestJson, out string normalizedDigest)
    {
        normalizedDigest = string.Empty;

        using var doc = JsonDocument.Parse(manifestJson);
        if (!doc.RootElement.TryGetProperty("layers", out var layersElement) || layersElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var layers = new List<ManifestLayer>();
        foreach (var layer in layersElement.EnumerateArray())
        {
            if (!layer.TryGetProperty("digest", out var digestElement) || digestElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var digest = NormalizeDigest(digestElement.GetString());
            if (digest is null)
            {
                continue;
            }

            var mediaType = layer.TryGetProperty("mediaType", out var mediaTypeElement) && mediaTypeElement.ValueKind == JsonValueKind.String
                ? mediaTypeElement.GetString()
                : null;

            long? size = null;
            if (layer.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize))
            {
                size = parsedSize;
            }

            layers.Add(new ManifestLayer(digest, mediaType, size));
        }

        if (layers.Count == 0)
        {
            return false;
        }

        var mediaTypeLayer = layers.FirstOrDefault(l =>
            !string.IsNullOrWhiteSpace(l.MediaType) &&
            l.MediaType.Contains("model", StringComparison.OrdinalIgnoreCase));
        if (mediaTypeLayer is not null)
        {
            normalizedDigest = mediaTypeLayer.Digest;
            return true;
        }

        if (layers.Count > 1 && layers.All(l => l.Size.HasValue))
        {
            normalizedDigest = layers
                .OrderByDescending(l => l.Size!.Value)
                .First()
                .Digest;
            return true;
        }

        normalizedDigest = layers[^1].Digest;
        return true;
    }

    private static string? NormalizeDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        var trimmed = digest.Trim();
        if (!trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hash = trimmed["sha256:".Length..];
        if (hash.Length == 0 || hash.Any(c => !char.IsLetterOrDigit(c)))
        {
            return null;
        }

        return $"sha256:{hash.ToLowerInvariant()}";
    }

    private static async Task<string> ComputeSha256Async(string modelPath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(modelPath);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<int> RunProcessStreamingAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IDictionary<string, string> env, Action<string> onOutput, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var pair in env)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();

        using var reg = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // no-op best effort
            }
        });

        var outputTask = Consume(process.StandardOutput, onOutput, ct);
        var errorTask = Consume(process.StandardError, onOutput, ct);

        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync());
        ct.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    private static async Task Consume(StreamReader reader, Action<string> onOutput, CancellationToken ct)
    {
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (!string.IsNullOrWhiteSpace(line))
            {
                onOutput(line);
            }
        }
    }
}

internal sealed record ManifestLayer(string Digest, string? MediaType, long? Size);

public sealed record PullModelResult(string Sha256, long SizeBytes);
