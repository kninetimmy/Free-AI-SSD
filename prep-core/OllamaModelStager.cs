namespace FreeAiSsd.PrepApp;

/// <summary>
/// MAC35: Mac-only host-stage helper for Ollama model pulls.
///
/// Driver: Ollama hardcodes <c>numDownloadParts = 16</c>
/// (re-verified upstream <c>server/download.go</c> through v0.23.2,
/// 2026-05) and exFAT FSKit on macOS 15+ cannot sustain 16 concurrent
/// writers on a single blob. The v1.3.14 mac field test of
/// <c>qwen2.5:7b</c> (4.7 GB) collapsed to ~5 MB/s with 290 stall events
/// over 19 minutes and Ollama's UI bouncing 35-60 % → 6 % — chunks made
/// local progress but the per-chunk byte-progress detector kept tripping
/// and restarting them. Direct Ollama on Windows over the same
/// connection downloads fine because NTFS handles the I/O pattern.
///
/// Strategy: pull into <c>~/Library/Caches/FreeAiSsd/ollama-staging</c>
/// (host APFS — no exFAT contention) and then sequentially copy the
/// manifest + referenced blobs to the SSD. Windows path stays direct;
/// the asymmetry is justified by the same shape as MAC34b's lsof-vs-
/// port-shift split — implementation diverges by platform constraint,
/// user-visible outcome converges.
///
/// Invariants preserved:
///   - Source-of-truth for installed models stays disk-truth on the
///     SSD (MAC33). The merge writes byte-identical layout to what a
///     direct pull would produce.
///   - Manifest-written-last: a cancelled or torn merge never leaves
///     a model "discoverable but corrupt" because the manifest is the
///     last file copied. Discovery code (<c>DiscoverModelsOnDisk</c>)
///     enumerates manifests, so no manifest = not discovered.
///   - Idempotent retry: per-blob skip-if-size-match + tmp-then-rename
///     so a re-run after a cancelled merge resumes cleanly without
///     re-copying intact blobs and without ever exposing a torn dest.
/// </summary>
public static class OllamaModelStager
{
    /// <summary>
    /// Per-user host staging root. Reused across Prep + Runner pulls
    /// (when MAC35 expands runner-side; today only Prep consumes this).
    /// Resolves to <c>~/Library/Caches/FreeAiSsd/ollama-staging</c> with
    /// the manifests/blobs subtree created on demand by Ollama itself.
    /// Creates the root directory if missing so the temp-server's
    /// <c>OLLAMA_MODELS</c> env var can point at it on first pull.
    /// </summary>
    public static string ResolveMacStagingRoot()
    {
        var home = Environment.GetEnvironmentVariable("HOME")
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            throw new InvalidOperationException(
                "Cannot resolve user home directory; HOME is unset and SpecialFolder.UserProfile returned empty.");
        }
        var root = Path.Combine(home, "Library", "Caches", "FreeAiSsd", "ollama-staging");
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// Refuses to start a pull when the staging volume doesn't have at
    /// least <c>2 × estimatedModelSizeBytes</c> free, with a 5 GB floor
    /// when the estimate is zero/unknown. Surfaces a clear error before
    /// the pull starts — failing mid-pull with a disk-full from APFS
    /// is a worse UX than failing the precheck.
    ///
    /// The 2x factor accounts for the staging copy + the SSD copy
    /// existing on the staging volume during the merge window (the
    /// copy reads from staging and writes to the SSD; the staging copy
    /// is only reclaimed on a separate cleanup pass if/when one lands).
    /// </summary>
    public static void EnsureStagingFreeSpace(string stagingRoot, long estimatedModelSizeBytes)
    {
        var required = estimatedModelSizeBytes > 0
            ? estimatedModelSizeBytes * 2L
            : 5L * 1024 * 1024 * 1024;

        long available;
        try
        {
            var drive = new DriveInfo(stagingRoot);
            if (!drive.IsReady)
            {
                // Treat "not ready" as a hard fail rather than skipping —
                // the pull can't write to a not-ready volume anyway.
                throw new InvalidOperationException(
                    $"Staging volume at {stagingRoot} is not ready.");
            }
            available = drive.AvailableFreeSpace;
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"Cannot inspect staging volume at {stagingRoot}: {ex.Message}", ex);
        }

        if (available < required)
        {
            var requiredGb = required / (1024.0 * 1024 * 1024);
            var availableGb = available / (1024.0 * 1024 * 1024);
            throw new IOException(
                $"Staging volume at {stagingRoot} has {availableGb:F1} GB free; pull requires {requiredGb:F1} GB. " +
                "Free up space or move ~/Library/Caches/FreeAiSsd to a larger volume.");
        }
    }

    /// <summary>
    /// Merges a fully-pulled staging tree into <paramref name="ssdModelsRoot"/>.
    /// Reads the manifest at <c>&lt;staging&gt;/manifests/registry.ollama.ai/library/&lt;name&gt;/&lt;tag&gt;</c>,
    /// copies each referenced blob to <c>&lt;ssdModelsRoot&gt;/blobs/sha256-&lt;hex&gt;</c>
    /// (skip when dest already exists at the source's exact size — the
    /// retry-after-cancel idempotence path), then copies the manifest
    /// last so a torn merge is invisible to <c>DiscoverModelsOnDisk</c>.
    ///
    /// Per-file cancel-safety uses a <c>&lt;dest&gt;.tmp</c> sidecar +
    /// atomic <see cref="File.Move(string, string, bool)"/> with overwrite:
    /// the dest path never holds partial bytes, and any leftover tmp
    /// from a prior cancelled merge is cleaned at the start of each
    /// copy so retries don't accumulate junk.
    /// </summary>
    public static async Task MergeToSsdAsync(
        string stagingRoot,
        string ssdModelsRoot,
        string modelTag,
        Action<string> onLog,
        CancellationToken ct)
    {
        // 2026-05-11 HF fix: delegate to ModelOperations' resolver so the
        // hf.co/Owner/Repo subtree is handled consistently with
        // FindModelBlobForModel / EstimatePartialProgress, AND so the
        // local IsSafeModelTag (lowercase-only) doesn't refuse HF tags
        // for having uppercase characters or slashes. The resolver's
        // own allowlist still rejects path traversal and unsafe
        // characters, so the hostile-tag refusal pin still passes.
        if (!ModelOperations.TryResolveOllamaManifestPath(modelTag, out var manifestSubdir, out var manifestTag))
            throw new InvalidOperationException($"Refusing to merge unsafe or malformed model tag '{modelTag}'.");

        var stagingManifest = Path.Combine(stagingRoot, "manifests", manifestSubdir, manifestTag);
        if (!File.Exists(stagingManifest))
            throw new FileNotFoundException(
                $"Staging manifest missing at {stagingManifest}. Pull may have failed silently.", stagingManifest);

        var stagingBlobs = Path.Combine(stagingRoot, "blobs");
        var ssdBlobs = Path.Combine(ssdModelsRoot, "blobs");
        var ssdManifestDir = Path.Combine(ssdModelsRoot, "manifests", manifestSubdir);
        Directory.CreateDirectory(ssdBlobs);
        Directory.CreateDirectory(ssdManifestDir);

        var manifestJson = await File.ReadAllTextAsync(stagingManifest, ct);
        var blobDigests = EnumerateBlobDigests(manifestJson).ToList();
        if (blobDigests.Count == 0)
            throw new InvalidOperationException(
                $"Staging manifest at {stagingManifest} declares no blob layers. Refusing to publish a hollow model.");

        // Manifest config blob (the "config" digest in the OCI envelope) is
        // referenced separately from layers but must also be copied or
        // Ollama refuses to load the model. Surface both.
        var configDigest = TrySelectConfigDigest(manifestJson);
        if (configDigest is not null && !blobDigests.Contains(configDigest, StringComparer.OrdinalIgnoreCase))
        {
            blobDigests.Add(configDigest);
        }

        onLog($"Merging {modelTag}: {blobDigests.Count} blob(s) staging → SSD.");

        for (var i = 0; i < blobDigests.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var digest = blobDigests[i];
            var blobFile = "sha256-" + digest;
            var src = Path.Combine(stagingBlobs, blobFile);
            var dest = Path.Combine(ssdBlobs, blobFile);

            if (!File.Exists(src))
            {
                throw new FileNotFoundException(
                    $"Staging blob {blobFile} missing — the staging tree is incomplete.", src);
            }

            var srcSize = new FileInfo(src).Length;
            if (File.Exists(dest) && new FileInfo(dest).Length == srcSize)
            {
                onLog($"Blob {i + 1}/{blobDigests.Count} already on SSD ({srcSize:N0} bytes); skip.");
                continue;
            }

            onLog($"Copying blob {i + 1}/{blobDigests.Count} ({srcSize:N0} bytes)…");
            await CopyFileAtomicAsync(src, dest, ct);
        }

        // Manifest written last: until this rename succeeds,
        // DiscoverModelsOnDisk does not enumerate this tag, so a
        // partial merge is invisible to the runner.
        var ssdManifestPath = Path.Combine(ssdManifestDir, manifestTag);
        ct.ThrowIfCancellationRequested();
        await CopyFileAtomicAsync(stagingManifest, ssdManifestPath, ct);
        onLog($"Merge complete: {modelTag} now resolvable on SSD.");
    }

    /// <summary>
    /// Cancellable, atomic file copy: writes to <c>&lt;dest&gt;.tmp</c>
    /// and then renames over <paramref name="dest"/>. On cancellation
    /// or any IO error, the tmp file is best-effort deleted before
    /// re-throw. Buffer size is 1 MB which is the same shape as the
    /// default <see cref="Stream.CopyToAsync(Stream)"/> uses for large
    /// streams — empirically a sweet spot for sequential SSD writes.
    /// </summary>
    private static async Task CopyFileAtomicAsync(string src, string dest, CancellationToken ct)
    {
        var tmp = dest + ".tmp";
        try
        {
            // Clean leftover tmp from a prior cancelled merge so File.Open
            // with FileMode.CreateNew doesn't false-positive.
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); }
                catch (Exception ex)
                {
                    throw new IOException(
                        $"Could not clear stale tmp file at {tmp}: {ex.Message}", ex);
                }
            }

            const int bufferSize = 1 << 20;
            await using (var srcStream = new FileStream(
                src, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true))
            await using (var dstStream = new FileStream(
                tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, useAsync: true))
            {
                await srcStream.CopyToAsync(dstStream, bufferSize, ct);
            }

            File.Move(tmp, dest, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch { /* best-effort cleanup; the next retry will clean it instead. */ }
            throw;
        }
    }

    /// <summary>
    /// Enumerates the layer digests referenced by an OCI manifest. Yields
    /// the bare hex (no "sha256:" prefix) so callers can build the on-disk
    /// "sha256-&lt;hex&gt;" filename directly.
    /// </summary>
    private static IEnumerable<string> EnumerateBlobDigests(string manifestJson)
    {
        using var doc = JsonDocument.Parse(manifestJson);
        if (!doc.RootElement.TryGetProperty("layers", out var layersElement) || layersElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }
        foreach (var layer in layersElement.EnumerateArray())
        {
            if (layer.TryGetProperty("digest", out var digestElement) && digestElement.ValueKind == JsonValueKind.String)
            {
                var hex = StripSha256Prefix(digestElement.GetString());
                if (hex is not null) yield return hex;
            }
        }
    }

    /// <summary>
    /// Returns the manifest's config-blob hex digest (the OCI "config"
    /// envelope), or null if the manifest doesn't declare one.
    /// </summary>
    private static string? TrySelectConfigDigest(string manifestJson)
    {
        using var doc = JsonDocument.Parse(manifestJson);
        if (!doc.RootElement.TryGetProperty("config", out var configElement) || configElement.ValueKind != JsonValueKind.Object)
            return null;
        if (!configElement.TryGetProperty("digest", out var digestElement) || digestElement.ValueKind != JsonValueKind.String)
            return null;
        return StripSha256Prefix(digestElement.GetString());
    }

    private static string? StripSha256Prefix(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        var trimmed = digest.Trim();
        if (!trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
        var hex = trimmed["sha256:".Length..].ToLowerInvariant();
        if (hex.Length == 0 || hex.Any(c => !char.IsLetterOrDigit(c))) return null;
        return hex;
    }

}
