using System.Net.Http;
using System.Text.Json;

namespace FreeAiSsd.Shared.Prereqs;

/// <summary>
/// Resolves voice-file hashes for Piper voices by querying the Hugging Face
/// tree API. Pure parser methods are split out from the network call so unit
/// tests can exercise the parser without touching the internet — mirrors the
/// pattern already used by <see cref="PrereqResolver"/>.
///
/// Trust model: HF returns the LFS <c>oid</c> for files stored under LFS,
/// which is the canonical SHA-256 of the file bytes. We fail closed if the
/// API is unreachable, malformed, or missing the file entry. We do not fall
/// back to HTTPS-only trust for the .onnx — the user explicitly opted into
/// LFS oid verification.
/// </summary>
public static class PiperResolver
{
    /// <summary>HF tree-listing API root.</summary>
    public const string HuggingFaceApiBase = "https://huggingface.co/api/models";

    /// <summary>HF download root for files (resolves the LFS pointer to bytes).</summary>
    public const string HuggingFaceResolveBase = "https://huggingface.co";

    /// <summary>
    /// Returns the HF tree API URL listing a single voice directory's files.
    /// </summary>
    public static string BuildTreeApiUrl(PiperVoice voice)
    {
        if (voice is null) throw new ArgumentNullException(nameof(voice));
        return $"{HuggingFaceApiBase}/{voice.HfRepo}/tree/main/{voice.HfPath}";
    }

    /// <summary>
    /// Returns the HF resolve URL for a file inside a voice directory. The
    /// <c>resolve/main</c> path follows LFS pointers and returns the raw bytes.
    /// </summary>
    public static string BuildFileResolveUrl(PiperVoice voice, string fileName)
    {
        if (voice is null) throw new ArgumentNullException(nameof(voice));
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("File name must be non-empty.", nameof(fileName));
        return $"{HuggingFaceResolveBase}/{voice.HfRepo}/resolve/main/{voice.HfPath}/{fileName}";
    }

    /// <summary>
    /// Fetches the HF tree listing for the voice and returns the LFS oid +
    /// size for the .onnx model. Fails closed if the API is unreachable,
    /// returns non-JSON, lists no LFS entry for the model file, or returns
    /// an oid of unexpected length.
    /// </summary>
    public static async Task<PiperVoiceResolution> ResolveVoiceAsync(
        HttpClient http, PiperVoice voice, CancellationToken ct = default)
    {
        if (http is null) throw new ArgumentNullException(nameof(http));
        if (voice is null) throw new ArgumentNullException(nameof(voice));

        var treeUrl = BuildTreeApiUrl(voice);
        string treeJson;
        try
        {
            treeJson = await http.GetStringAsync(treeUrl, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to fetch Hugging Face tree for {voice.Id} at {treeUrl}: {ex.Message}", ex);
        }

        return ParseVoiceTree(treeJson, voice);
    }

    /// <summary>
    /// Pure parser for the HF tree API JSON document. The document is a JSON
    /// array of file/directory entries; LFS-tracked files have a nested
    /// <c>lfs.oid</c> (lowercase hex SHA-256) and <c>lfs.size</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the document
    /// is not a JSON array, the model file is missing, the file has no LFS
    /// pointer (i.e. is not LFS-tracked), or the oid is not 64 hex chars.</exception>
    public static PiperVoiceResolution ParseVoiceTree(string treeJson, PiperVoice voice)
    {
        if (voice is null) throw new ArgumentNullException(nameof(voice));

        using var doc = JsonDocument.Parse(treeJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Hugging Face tree for {voice.Id} is not a JSON array (got {root.ValueKind}).");
        }

        var expectedOnnxPath = $"{voice.HfPath}/{voice.OnnxFileName}";
        var expectedJsonPath = $"{voice.HfPath}/{voice.OnnxJsonFileName}";

        string? onnxOid = null;
        long onnxSize = 0;
        long jsonSize = 0;
        var jsonFound = false;

        foreach (var entry in root.EnumerateArray())
        {
            if (!entry.TryGetProperty("path", out var pathEl)) continue;
            var path = pathEl.GetString();
            if (string.IsNullOrWhiteSpace(path)) continue;

            if (string.Equals(path, expectedOnnxPath, StringComparison.Ordinal))
            {
                if (!entry.TryGetProperty("lfs", out var lfs) || lfs.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException(
                        $"Hugging Face entry for {expectedOnnxPath} has no LFS pointer — refusing to install an unverified voice.");
                }
                var oid = lfs.TryGetProperty("oid", out var o) ? o.GetString() : null;
                var size = lfs.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0L;
                if (string.IsNullOrWhiteSpace(oid) || oid.Length != 64)
                {
                    throw new InvalidOperationException(
                        $"Hugging Face LFS oid for {expectedOnnxPath} is missing or has wrong length: '{oid}'.");
                }
                onnxOid = oid.ToLowerInvariant();
                onnxSize = size;
            }
            else if (string.Equals(path, expectedJsonPath, StringComparison.Ordinal))
            {
                jsonFound = true;
                if (entry.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv)) jsonSize = sv;
            }
        }

        if (onnxOid is null)
        {
            throw new InvalidOperationException(
                $"Hugging Face tree for {voice.Id} has no entry for {expectedOnnxPath}.");
        }
        if (!jsonFound)
        {
            throw new InvalidOperationException(
                $"Hugging Face tree for {voice.Id} has no entry for {expectedJsonPath}.");
        }

        return new PiperVoiceResolution(
            Voice: voice,
            OnnxUrl: BuildFileResolveUrl(voice, voice.OnnxFileName),
            OnnxSha256: onnxOid,
            OnnxSizeBytes: onnxSize,
            OnnxJsonUrl: BuildFileResolveUrl(voice, voice.OnnxJsonFileName),
            OnnxJsonSha256: voice.OnnxJsonSha256,
            OnnxJsonSizeBytes: jsonSize,
            TrustNote: $"Vendor SHA-256 from Hugging Face LFS oid for {voice.HfRepo}/{voice.HfPath}.");
    }
}

/// <summary>
/// Result of resolving a single Piper voice from Hugging Face: download URLs
/// and expected SHA-256s for the .onnx and .onnx.json files. The .onnx hash
/// is the live HF LFS oid; the .onnx.json hash is the static catalog pin
/// (HF tree API does not expose content SHA-256 for non-LFS files).
/// </summary>
public sealed record PiperVoiceResolution(
    PiperVoice Voice,
    string OnnxUrl,
    string OnnxSha256,
    long OnnxSizeBytes,
    string OnnxJsonUrl,
    string OnnxJsonSha256,
    long OnnxJsonSizeBytes,
    string TrustNote);
