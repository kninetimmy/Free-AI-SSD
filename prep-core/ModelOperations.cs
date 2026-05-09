using System.Diagnostics;
using FreeAiSsd.Shared.Helpers;

namespace FreeAiSsd.PrepApp;

/// <summary>
/// Manages Ollama model lifecycle operations: pulling (downloading), verifying,
/// deleting, and discovering models on the SSD. Interacts with the Ollama CLI
/// binary to perform model operations, and parses Ollama's OCI-compatible
/// manifest format to locate model blobs for integrity verification.
/// </summary>
public sealed class ModelOperations
{
    /// <summary>
    /// Downloads a model from the Ollama registry using the bundled Ollama CLI.
    /// Sets OLLAMA_MODELS to redirect storage to the SSD's models directory.
    /// After pulling, locates the model's primary blob and computes its SHA-256 hash.
    /// </summary>
    /// <param name="ollamaExe">Path to the Ollama executable.</param>
    /// <param name="modelRoot">Path to the SSD's models directory.</param>
    /// <param name="modelTag">Model tag to pull (e.g., "llama3.2:3b").</param>
    /// <param name="onLog">Callback for streaming pull progress to the UI.</param>
    /// <param name="ct">Cancellation token for aborting the download.</param>
    /// <returns>Pull result containing the SHA-256 hash and file size of the model blob.</returns>
    public async Task<PullModelResult> PullModelAsync(string ollamaExe, string modelRoot, string modelTag, Action<string> onLog, CancellationToken ct, string? ollamaHost = null, Action<string>? onProgress = null)
    {
        var env = new Dictionary<string, string>
        {
            ["OLLAMA_MODELS"] = modelRoot
        };
        if (ollamaHost is not null)
            env["OLLAMA_HOST"] = ollamaHost;

        // MAC31: onProgress is opt-in. When set, Ollama's TUI progress
        // lines (after ANSI strip) are routed there instead of onLog so
        // the PrepApp can render a single in-place progress line. When
        // null, callers see the legacy behavior (every tick lands in the
        // log surface) so existing call sites don't have to change.
        var exitCode = await RunProcessStreamingAsync(ollamaExe, BuildOllamaArgs("pull", modelTag), Path.GetDirectoryName(ollamaExe)!, env, onLog, ct, onProgress);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Failed to pull model {modelTag}. Exit code: {exitCode}");
        }

        // After successful pull, locate the model blob to compute its integrity hash.
        var modelFile = FindModelBlobForModel(modelRoot, modelTag)
                        ?? throw new FileNotFoundException($"Unable to locate model blob for {modelTag} in {modelRoot}.");

        var sha256 = await ComputeSha256Async(modelFile, ct);
        var size = new FileInfo(modelFile).Length;
        return new PullModelResult(sha256, size);
    }

    /// <summary>
    /// Verifies a model's integrity by comparing its blob's SHA-256 hash
    /// against the expected hash stored in the config.
    /// </summary>
    /// <param name="modelRoot">Path to the SSD's models directory.</param>
    /// <param name="modelTag">Model tag to verify.</param>
    /// <param name="expectedHash">Expected SHA-256 hash from the config.</param>
    /// <param name="onLog">Callback for verification status messages.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the hash matches; false if the blob is missing or hash differs.</returns>
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

    /// <summary>
    /// Deletes a model from disk using the Ollama CLI "rm" command.
    /// </summary>
    public async Task DeleteModelAsync(string ollamaExe, string modelRoot, string modelTag, Action<string> onLog, CancellationToken ct, string? ollamaHost = null)
    {
        var env = new Dictionary<string, string>
        {
            ["OLLAMA_MODELS"] = modelRoot
        };
        if (ollamaHost is not null)
            env["OLLAMA_HOST"] = ollamaHost;

        var exitCode = await RunProcessStreamingAsync(ollamaExe, BuildOllamaArgs("rm", modelTag), Path.GetDirectoryName(ollamaExe)!, env, onLog, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Failed to delete model {modelTag} from disk. Exit code: {exitCode}");
        }
    }

    /// <summary>
    /// Scans the models/manifests directory to discover all models present on disk.
    /// Ollama stores manifests in a nested directory structure:
    /// manifests/registry.ollama.ai/library/{model}/{tag}
    /// This method reconstructs the "model:tag" format from the directory structure.
    /// </summary>
    /// <param name="modelRoot">Path to the SSD's models directory.</param>
    /// <returns>Sorted set of discovered model tags (e.g., "llama3.2:3b").</returns>
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
            // MAC35a: macOS auto-creates AppleDouble companion files
            // (`._<name>`) on filesystems without native xattr support
            // (exFAT, FAT32, SMB). Their first byte is the AppleDouble
            // magic 0x00 0x05 0x16 0x07, so JsonDocument.Parse on them
            // crashes the readiness check. Skip any leaf starting with
            // `._` — Ollama tag/name segments can't legally start with
            // a dot, so we can never false-skip a real manifest.
            if (IsAppleDoubleSidecar(manifestPath))
            {
                continue;
            }

            var relative = Path.GetRelativePath(manifestsPath, manifestPath);
            var parts = relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            // Last two path segments are the model name and tag.
            var modelId = $"{parts[^2]}:{parts[^1]}";
            discovered.Add(modelId);
        }

        return discovered.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Locates the primary model blob file for a given model tag by reading
    /// its OCI manifest and finding the layer with the "model" media type.
    /// Returns the full path to the blob file, or null if not found.
    /// </summary>
    /// <param name="modelRoot">Path to the SSD's models directory.</param>
    /// <param name="model">Model tag (e.g., "llama3:8b").</param>
    /// <returns>Full path to the model blob file, or null.</returns>
    public static string? FindModelBlobForModel(string modelRoot, string model)
    {
        if (!TryParseModelReference(model, out var modelName, out var manifestTag))
        {
            return null;
        }

        var manifestsPath = Path.Combine(modelRoot, "manifests", "registry.ollama.ai", "library", modelName);
        if (!Directory.Exists(manifestsPath))
        {
            return null;
        }

        // MAC35a: skip AppleDouble sidecars (`._<tag>`). The pattern
        // passed to EnumerateFiles is the literal tag, but on macOS-
        // exFAT volumes the directory may also contain `._<tag>`
        // companions; without the filter, a future code path using a
        // wildcard pattern would resolve to the AppleDouble blob and
        // its 0x00-prefixed contents would crash JsonDocument.Parse
        // below. Belt-and-braces with the DiscoverModelsOnDisk filter.
        var manifest = Directory.EnumerateFiles(manifestsPath, manifestTag, SearchOption.TopDirectoryOnly)
            .FirstOrDefault(p => !IsAppleDoubleSidecar(p));
        if (manifest is null)
        {
            return null;
        }

        var content = File.ReadAllText(manifest);
        if (!TrySelectModelLayerDigest(content, out var normalizedDigest))
        {
            return null;
        }

        // Blob filenames replace ":" with "-" (e.g., "sha256-abcdef...").
        var blob = Path.Combine(modelRoot, "blobs", normalizedDigest.Replace(':', '-'));
        return File.Exists(blob) ? blob : null;
    }

    /// <summary>
    /// Builds a safe argument list for the Ollama CLI. Uses ArgumentList
    /// (not string concatenation) to prevent shell injection with model tags
    /// that may contain special characters.
    /// </summary>
    internal static IReadOnlyList<string> BuildOllamaArgs(string command, string modelTag)
        => new[] { command, modelTag };

    /// <summary>
    /// Parses a model reference string "name:tag" into its components.
    /// Handles the last colon as the separator (supports names with colons).
    /// </summary>
    private static bool TryParseModelReference(string model, out string modelName, out string tag)
    {
        modelName = string.Empty;
        tag = string.Empty;

        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var separatorIndex = model.LastIndexOf(':');
        if (separatorIndex <= 0 || separatorIndex >= model.Length - 1)
        {
            return false;
        }

        modelName = model[..separatorIndex].Trim();
        tag = model[(separatorIndex + 1)..].Trim();
        return modelName.Length > 0 && tag.Length > 0;
    }

    /// <summary>
    /// Selects the correct layer digest from an OCI manifest JSON.
    /// Priority order:
    /// 1. Layer with media type containing "model" (e.g., "application/vnd.ollama.image.layer.model").
    /// 2. Largest layer by size (if all layers report sizes).
    /// 3. Last layer in the array (fallback).
    /// This ensures we identify the actual model weights blob, not config or template layers.
    /// </summary>
    internal static bool TrySelectModelLayerDigest(string manifestJson, out string normalizedDigest)
    {
        normalizedDigest = string.Empty;

        // MAC35a: a malformed or non-JSON manifest must not crash the
        // caller. EstimatePartialProgress already wraps its parse in
        // try/catch; mirror that here so readiness fails gracefully on
        // a corrupt manifest instead of aborting the entire check.
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(manifestJson);
        }
        catch (JsonException)
        {
            return false;
        }

        using (doc)
        {
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

        // Priority 1: Layer with "model" in its media type.
        var mediaTypeLayer = layers.FirstOrDefault(l =>
            !string.IsNullOrWhiteSpace(l.MediaType) &&
            l.MediaType.Contains("model", StringComparison.OrdinalIgnoreCase));
        if (mediaTypeLayer is not null)
        {
            normalizedDigest = mediaTypeLayer.Digest;
            return true;
        }

        // Priority 2: Largest layer by size.
        if (layers.Count > 1 && layers.All(l => l.Size.HasValue))
        {
            normalizedDigest = layers
                .OrderByDescending(l => l.Size!.Value)
                .First()
                .Digest;
            return true;
        }

        // Priority 3: Last layer (fallback when sizes are unknown).
        normalizedDigest = layers[^1].Digest;
        return true;
        }
    }

    /// <summary>
    /// MAC35a: returns true when <paramref name="path"/>'s leaf
    /// filename starts with `._`, the AppleDouble companion-file
    /// prefix macOS auto-creates on filesystems without native xattr
    /// support (exFAT, FAT32, SMB). These files contain the
    /// AppleDouble magic header (0x00 0x05 0x16 0x07) — first byte
    /// 0x00 — so feeding them to <see cref="JsonDocument.Parse(string)"/>
    /// throws "0x00 is an invalid start of a value". Ollama tag and
    /// name segments cannot legally start with a dot, so this filter
    /// can never false-skip a real manifest or blob.
    /// </summary>
    private static bool IsAppleDoubleSidecar(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("._", StringComparison.Ordinal);
    }

    /// <summary>
    /// Normalizes a digest string to lowercase "sha256:hex" format.
    /// Rejects digests that don't start with "sha256:" or contain non-alphanumeric characters.
    /// </summary>
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

    /// <summary>
    /// Asynchronously computes the SHA-256 hash of a model blob file.
    /// Returns the hash as a lowercase hex string.
    /// </summary>
    private static Task<string> ComputeSha256Async(string modelPath, CancellationToken ct) =>
        CryptoUtils.ComputeSha256HexAsync(modelPath, ct);

    /// <summary>
    /// Runs an external process with streaming stdout/stderr output.
    /// Uses ArgumentList (not Arguments string) for safe argument passing.
    /// Registers a cancellation callback that kills the process tree on cancellation.
    /// </summary>
    private static async Task<int> RunProcessStreamingAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IDictionary<string, string> env, Action<string> onOutput, CancellationToken ct, Action<string>? onProgress = null)
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

        // Use ArgumentList for safe argument passing (prevents shell injection).
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();

        // Register cancellation to kill the process tree if the user cancels.
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
                // Best-effort kill; process may have already exited.
            }
        });

        // Consume stdout and stderr concurrently to prevent buffer deadlocks.
        // Consume uses ReadLineAsync (not the blocking EndOfStream property),
        // so both tasks yield immediately and resume on the caller's sync context.
        var outputTask = Consume(process.StandardOutput, onOutput, ct, onProgress);
        var errorTask = Consume(process.StandardError, onOutput, ct, onProgress);

        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync());
        ct.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    /// <summary>
    /// Reads lines from a stream reader asynchronously, invoking the callback
    /// for each non-empty line until EOF.
    /// Uses ReadLineAsync (returns null at EOF) instead of the EndOfStream property,
    /// which is synchronous and blocks the calling thread when no data is available.
    ///
    /// MAC31: when <paramref name="onProgress"/> is provided, the line is
    /// run through <see cref="OllamaPullProgressFilter"/> first.
    /// Lines that match Ollama's progress shape (<c>pulling &lt;hash&gt;... NN%</c>)
    /// are routed to onProgress instead of onOutput so the PrepApp UI can
    /// render them as a single in-place progress line. ANSI cursor-rewrite
    /// escapes are always stripped before forwarding so the log surface
    /// stays human-readable. A throwing onProgress is contained so a
    /// misbehaving UI dispatcher cannot leave the pipe unread.
    /// </summary>
    private static async Task Consume(StreamReader reader, Action<string> onOutput, CancellationToken ct, Action<string>? onProgress = null)
    {
        try
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                var cleaned = onProgress is null
                    ? line
                    : OllamaPullProgressFilter.Clean(line);

                if (string.IsNullOrWhiteSpace(cleaned))
                {
                    continue;
                }

                if (onProgress is not null && OllamaPullProgressFilter.IsProgressLine(cleaned))
                {
                    try { onProgress(cleaned); }
                    catch
                    {
                        // A misbehaving onProgress must not stop the
                        // consumer — same invariant as the onLog catch
                        // in OllamaServerHandle.ConsumeAsync.
                    }
                    continue;
                }

                try { onOutput(cleaned); }
                catch
                {
                    // Same containment as onProgress above.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Process killed or cancellation requested; stop reading.
        }
    }

    /// <summary>
    /// MAC31: estimates the fraction of <paramref name="modelTag"/>'s
    /// expected blob payload that's already on disk as
    /// <c>sha256-&lt;hex&gt;-partial-N</c> files under
    /// <c>&lt;modelsRoot&gt;/blobs/</c>. Used to seed the PrepApp's
    /// progress display before invoking <c>ollama pull</c> so a retry
    /// after a cancelled or interrupted pull surfaces "Resuming from
    /// 43%..." instead of starting at 0% (which is what the user sees
    /// today even though Ollama IS resumable).
    ///
    /// Returns 0.0 if no manifest exists yet, if the manifest is
    /// malformed, or if the blobs directory is missing. Returns 1.0
    /// when every layer has a fully-downloaded blob present. The model
    /// tag is validated against an allowlist so this method cannot be
    /// coerced into a path-traversal read via a hostile tag.
    /// </summary>
    public static double EstimatePartialProgress(string modelsRoot, string modelTag)
    {
        if (string.IsNullOrWhiteSpace(modelsRoot) || !Directory.Exists(modelsRoot))
        {
            return 0.0;
        }

        if (!IsSafeModelTag(modelTag))
        {
            return 0.0;
        }

        if (!TryParseModelReference(modelTag, out var modelName, out var manifestTag))
        {
            return 0.0;
        }

        var manifestPath = Path.Combine(modelsRoot, "manifests", "registry.ollama.ai", "library", modelName, manifestTag);
        if (!File.Exists(manifestPath))
        {
            return 0.0;
        }

        long totalBytes = 0;
        long downloadedBytes = 0;
        var blobsDir = Path.Combine(modelsRoot, "blobs");
        var blobsDirExists = Directory.Exists(blobsDir);

        try
        {
            var content = File.ReadAllText(manifestPath);
            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("layers", out var layersElement) || layersElement.ValueKind != JsonValueKind.Array)
            {
                return 0.0;
            }

            foreach (var layer in layersElement.EnumerateArray())
            {
                if (!layer.TryGetProperty("size", out var sizeElement) || !sizeElement.TryGetInt64(out var layerSize) || layerSize <= 0)
                {
                    continue;
                }
                totalBytes += layerSize;

                if (!blobsDirExists)
                {
                    continue;
                }

                if (!layer.TryGetProperty("digest", out var digestElement) || digestElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var digestRaw = digestElement.GetString();
                if (string.IsNullOrEmpty(digestRaw) || !digestRaw.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var digestSuffix = "sha256-" + digestRaw["sha256:".Length..].ToLowerInvariant();

                var fullBlob = Path.Combine(blobsDir, digestSuffix);
                if (File.Exists(fullBlob))
                {
                    downloadedBytes += new FileInfo(fullBlob).Length;
                    continue;
                }

                // Partial blobs Ollama writes mid-download. Multiple
                // partials per layer are possible (parallel chunks); sum
                // them all but cap at the layer's expected size so a
                // ragged retry can't produce > 100%.
                long partialSum = 0;
                foreach (var partial in Directory.EnumerateFiles(blobsDir, digestSuffix + "-partial*"))
                {
                    try { partialSum += new FileInfo(partial).Length; }
                    catch { /* best-effort; race with Ollama is fine */ }
                }
                downloadedBytes += Math.Min(partialSum, layerSize);
            }
        }
        catch
        {
            // Malformed manifest, IO error, or partial-file race — fall
            // through to a 0.0 seed. The pull still works; the user
            // just sees the bar start at 0% and climb from there.
            return 0.0;
        }

        if (totalBytes <= 0) return 0.0;
        var fraction = (double)downloadedBytes / totalBytes;
        return Math.Clamp(fraction, 0.0, 1.0);
    }

    /// <summary>
    /// MAC31: bounds <see cref="EstimatePartialProgress"/>'s path
    /// construction to the same character set Ollama tags use
    /// (lowercase alphanumerics, dot, underscore, hyphen, optional
    /// <c>:tag</c> suffix). Anything outside that — including <c>..</c>,
    /// path separators, or whitespace — fails closed. Mirrors the
    /// PathGuards posture for user-supplied path segments.
    /// </summary>
    private static bool IsSafeModelTag(string modelTag)
    {
        if (string.IsNullOrWhiteSpace(modelTag)) return false;
        foreach (var c in modelTag)
        {
            var ok = (c >= 'a' && c <= 'z')
                  || (c >= '0' && c <= '9')
                  || c == '.' || c == '_' || c == '-' || c == ':';
            if (!ok) return false;
        }
        return !modelTag.Contains("..", StringComparison.Ordinal);
    }
}

/// <summary>
/// Internal representation of a layer entry from an OCI manifest,
/// used during model blob resolution.
/// </summary>
internal sealed record ManifestLayer(string Digest, string? MediaType, long? Size);

/// <summary>
/// Result of a successful model pull, containing the SHA-256 hash
/// and size of the model's primary blob file.
/// </summary>
public sealed record PullModelResult(string Sha256, long SizeBytes);
