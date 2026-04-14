using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using FreeAiSsd.Shared.Helpers;

namespace FreeAiSsd.Shared.Prereqs;

/// <summary>
/// Result of resolving a prerequisite's current upstream metadata: the exact
/// versioned download URL and (when the upstream publishes one) a vendor hash
/// used to verify the downloaded bytes.
/// </summary>
/// <param name="Version">Human-readable version string (e.g. "8.0.18", "v0.20.7").</param>
/// <param name="Url">Fully-qualified HTTPS download URL. Never blank.</param>
/// <param name="Hash">Lowercase hex vendor-published hash. Null when no hash is
/// published by the upstream (VC++ aka.ms path). When non-null, the download
/// step MUST verify and refuse to install on mismatch.</param>
/// <param name="HashAlgorithm">"SHA256" or "SHA512". Ignored when Hash is null.</param>
/// <param name="TrustNote">Short free-form description of the trust model used
/// for this resolution (vendor hash, HTTPS-only permalink, etc.) — surfaced in
/// the prereq manifest so downstream auditors know what was verified.</param>
public sealed record PrereqResolution(
    string Version,
    string Url,
    string? Hash,
    string HashAlgorithm,
    string TrustNote);

/// <summary>
/// Phase-A hardening: replaces hardcoded URL + SHA pins with runtime discovery
/// from vendor-published metadata endpoints. Every resolver is HTTPS-only, fails
/// closed on any network / parse / hash error, and never falls back to a weaker
/// trust mode. The same logic is shared between PrepApp (runtime update path)
/// and CI (offline-bundle build path) so the two never drift.
///
/// Trust model, per upstream:
///   - .NET 8 Desktop Runtime: SHA-512 from Microsoft's releases.json metadata feed.
///     We verify HTTPS + exact filename + vendor-published SHA-512 per release file.
///   - VC++ Redistributable: Microsoft ships an evergreen HTTPS permalink
///     (https://aka.ms/vs/17/release/vc_redist.x64.exe) and does NOT publish a
///     stable per-version hash at a predictable URL. We rely on HTTPS trust to
///     Microsoft's CDN, record the observed SHA-256, and fail the build on any
///     network error. When a hash source becomes available, promote to verified.
///   - Ollama (macOS): SHA-256 from the per-release sha256sum.txt asset next to
///     the binary in the GitHub release. Version is discovered from the GitHub
///     releases/latest API. We fail closed if the sha256sum.txt asset is missing,
///     unparseable, or does not contain an entry for the target filename.
/// </summary>
public static class PrereqResolver
{
    /// <summary>Filename of the latest-stable .NET 8 Desktop Runtime x64 installer.</summary>
    public const string DotnetDesktopWindowsFilenamePrefix = "windowsdesktop-runtime-";

    /// <summary>HTTPS-only aka.ms permalink for the x64 VC++ Redistributable.</summary>
    public const string VcRedistX64PermalinkUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

    /// <summary>Microsoft's per-channel release metadata feed for .NET 8.</summary>
    public const string DotnetReleasesJsonUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json";

    /// <summary>GitHub API endpoint for the latest Ollama release.</summary>
    public const string OllamaLatestReleaseApiUrl = "https://api.github.com/repos/ollama/ollama/releases/latest";

    /// <summary>
    /// Resolves the exact versioned x64 .NET 8 Desktop Runtime installer for the
    /// latest stable 8.0.x release, with its vendor-published SHA-512.
    /// </summary>
    /// <param name="http">Pre-configured HttpClient (caller owns lifetime).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A resolution whose Url is HTTPS, Hash is a 128-char lowercase hex
    /// SHA-512 string, and HashAlgorithm is "SHA512".</returns>
    /// <exception cref="InvalidOperationException">Thrown when the metadata feed
    /// is unreachable, unparseable, missing the expected file entry, or does not
    /// publish a SHA-512 for the target file.</exception>
    public static async Task<PrereqResolution> ResolveDotnet8DesktopX64Async(
        HttpClient http, CancellationToken ct = default)
    {
        if (http is null) throw new ArgumentNullException(nameof(http));

        string json;
        try
        {
            json = await http.GetStringAsync(DotnetReleasesJsonUrl, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to fetch .NET 8 release metadata from {DotnetReleasesJsonUrl}: {ex.Message}", ex);
        }

        return ParseDotnet8DesktopX64(json);
    }

    /// <summary>
    /// Pure parser for the Microsoft .NET 8 releases.json document. Split out
    /// from the network call so unit tests can exercise the parser without
    /// touching the internet.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown on any structural
    /// failure: missing fields, no stable 8.0.x release, no windowsdesktop-runtime
    /// x64 file, or missing/blank SHA-512.</exception>
    public static PrereqResolution ParseDotnet8DesktopX64(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("latest-release", out var latestRelease))
        {
            throw new InvalidOperationException(".NET releases.json missing 'latest-release' field.");
        }

        var latestVersion = latestRelease.GetString()
            ?? throw new InvalidOperationException(".NET releases.json 'latest-release' is null.");

        if (!latestVersion.StartsWith("8.0.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unexpected .NET latest-release value '{latestVersion}' — expected 8.0.x.");
        }

        // Skip preview / rc builds — we only ship latest stable per user requirement.
        if (latestVersion.Contains('-'))
        {
            throw new InvalidOperationException(
                $".NET latest-release '{latestVersion}' is a preview/rc build; refusing to pin a non-stable runtime.");
        }

        if (!root.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(".NET releases.json missing 'releases' array.");
        }

        foreach (var release in releases.EnumerateArray())
        {
            if (!release.TryGetProperty("release-version", out var versionEl)) continue;
            if (!string.Equals(versionEl.GetString(), latestVersion, StringComparison.Ordinal)) continue;

            if (!release.TryGetProperty("windowsdesktop", out var winDesktop) ||
                !winDesktop.TryGetProperty("files", out var files) ||
                files.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    $".NET release '{latestVersion}' is missing windowsdesktop.files.");
            }

            foreach (var file in files.EnumerateArray())
            {
                var name = file.TryGetProperty("name", out var n) ? n.GetString() : null;
                var rid  = file.TryGetProperty("rid",  out var r) ? r.GetString() : null;
                if (!string.Equals(rid, "win-x64", StringComparison.Ordinal)) continue;
                if (name is null || !name.StartsWith(DotnetDesktopWindowsFilenamePrefix, StringComparison.Ordinal)) continue;
                if (!name.EndsWith(".exe", StringComparison.Ordinal)) continue;

                var url = file.TryGetProperty("url", out var u) ? u.GetString() : null;
                var hash = file.TryGetProperty("hash", out var h) ? h.GetString() : null;

                if (string.IsNullOrWhiteSpace(url))
                    throw new InvalidOperationException($".NET file '{name}' has no url.");
                if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($".NET file url is not HTTPS: {url}");
                if (string.IsNullOrWhiteSpace(hash))
                    throw new InvalidOperationException($".NET file '{name}' has no SHA-512 hash.");

                return new PrereqResolution(
                    Version: latestVersion,
                    Url: url,
                    Hash: hash.ToLowerInvariant(),
                    HashAlgorithm: "SHA512",
                    TrustNote: "Vendor-published SHA-512 from builds.dotnet.microsoft.com releases.json.");
            }

            throw new InvalidOperationException(
                $".NET release '{latestVersion}' has no win-x64 windowsdesktop-runtime-*.exe file entry.");
        }

        throw new InvalidOperationException(
            $".NET releases.json has no entry matching latest-release '{latestVersion}'.");
    }

    /// <summary>
    /// Returns the HTTPS-only evergreen permalink for the x64 VC++ Redistributable.
    /// Microsoft does not publish a stable per-version SHA at a predictable URL;
    /// trust is therefore anchored to HTTPS to Microsoft's CDN. The observed hash
    /// is still recorded in the manifest for audit.
    /// </summary>
    public static PrereqResolution ResolveVcRedistX64()
    {
        return new PrereqResolution(
            Version: "evergreen-aka-ms",
            Url: VcRedistX64PermalinkUrl,
            Hash: null,
            HashAlgorithm: "SHA256",
            TrustNote: "HTTPS-only trust to Microsoft aka.ms permalink; no vendor-published per-version hash.");
    }

    /// <summary>
    /// Resolves the latest Ollama macOS universal ZIP by querying the GitHub
    /// releases API and fetching the release's sha256sum.txt asset.
    /// </summary>
    /// <param name="http">HttpClient. MUST have a "User-Agent" default header set
    /// (GitHub's API rejects requests without one).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="preferredAssetNames">Asset filenames to look for in the release,
    /// in priority order. Ollama capitalized the asset name in v0.20.7+ so we
    /// try both "Ollama-darwin.zip" and "ollama-darwin.zip".</param>
    public static async Task<PrereqResolution> ResolveLatestOllamaMacAsync(
        HttpClient http, CancellationToken ct = default,
        IReadOnlyList<string>? preferredAssetNames = null)
    {
        if (http is null) throw new ArgumentNullException(nameof(http));
        preferredAssetNames ??= new[] { "Ollama-darwin.zip", "ollama-darwin.zip" };

        string releaseJson;
        try
        {
            releaseJson = await http.GetStringAsync(OllamaLatestReleaseApiUrl, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to fetch Ollama latest release metadata: {ex.Message}", ex);
        }

        var (tag, assetUrl, assetName, shaAssetUrl) = ParseOllamaLatestRelease(releaseJson, preferredAssetNames);

        string shaSumText;
        try
        {
            shaSumText = await http.GetStringAsync(shaAssetUrl, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to fetch Ollama sha256sum.txt from {shaAssetUrl}: {ex.Message}", ex);
        }

        var hash = ParseSha256SumForFile(shaSumText, assetName);

        return new PrereqResolution(
            Version: tag,
            Url: assetUrl,
            Hash: hash,
            HashAlgorithm: "SHA256",
            TrustNote: $"Vendor-published SHA-256 from Ollama release {tag} sha256sum.txt asset.");
    }

    /// <summary>
    /// Pure parser for the GitHub "releases/latest" JSON. Returns the tag name,
    /// the download URL for the first matching preferred asset, the asset name
    /// that matched, and the URL of the release's sha256sum.txt asset.
    /// </summary>
    public static (string Tag, string AssetUrl, string AssetName, string ShaAssetUrl)
        ParseOllamaLatestRelease(string json, IReadOnlyList<string> preferredAssetNames)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag))
            throw new InvalidOperationException("Ollama release metadata missing tag_name.");

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Ollama release metadata missing assets array.");

        string? matchUrl = null;
        string? matchName = null;
        string? shaAssetUrl = null;

        foreach (var preferred in preferredAssetNames)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
                if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Ollama asset url is not HTTPS: {url}");

                if (string.Equals(name, preferred, StringComparison.Ordinal))
                {
                    matchUrl = url;
                    matchName = name;
                    break;
                }
            }
            if (matchUrl is not null) break;
        }

        if (matchUrl is null || matchName is null)
        {
            throw new InvalidOperationException(
                $"Ollama release {tag} has no asset matching any of: {string.Join(", ", preferredAssetNames)}.");
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
            if (string.Equals(name, "sha256sum.txt", StringComparison.Ordinal))
            {
                if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Ollama sha256sum.txt url is not HTTPS: {url}");
                shaAssetUrl = url;
                break;
            }
        }

        if (shaAssetUrl is null)
            throw new InvalidOperationException($"Ollama release {tag} is missing sha256sum.txt asset.");

        return (tag!, matchUrl, matchName, shaAssetUrl);
    }

    /// <summary>
    /// Parses a sha256sum.txt document (one "hash  filename" line per file) and
    /// returns the lowercase hex hash for the file whose name matches exactly,
    /// tolerating both "hash  name" and "hash  ./name" forms.
    /// </summary>
    public static string ParseSha256SumForFile(string shaSumText, string filename)
    {
        foreach (var rawLine in shaSumText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) continue;

            var hash = parts[0].Trim();
            var name = parts[1].Trim();
            if (name.StartsWith("./", StringComparison.Ordinal)) name = name.Substring(2);
            if (name.StartsWith("*", StringComparison.Ordinal)) name = name.Substring(1); // binary-mode marker

            if (string.Equals(name, filename, StringComparison.Ordinal))
            {
                if (hash.Length != 64)
                    throw new InvalidOperationException(
                        $"sha256sum entry for '{filename}' has invalid length: '{hash}'.");
                return hash.ToLowerInvariant();
            }
        }

        throw new InvalidOperationException(
            $"sha256sum.txt has no entry for '{filename}'.");
    }

    /// <summary>
    /// Downloads <paramref name="resolution"/>.Url to <paramref name="destinationPath"/>,
    /// streams to a temp .download file, verifies the configured hash when the
    /// resolution includes one, and atomically moves to the final path on success.
    /// Fails closed on any network error, any HTTPS refusal, or any hash mismatch.
    /// </summary>
    /// <returns>The SHA-256 of the downloaded file (always computed and returned
    /// for manifest auditing even when the primary verification used SHA-512).</returns>
    public static async Task<string> DownloadAndVerifyAsync(
        HttpClient http, PrereqResolution resolution, string destinationPath,
        Action<string>? log = null, CancellationToken ct = default)
    {
        if (!resolution.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to download over non-HTTPS URL: {resolution.Url}");

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var tempPath = destinationPath + ".download";
        if (File.Exists(tempPath)) File.Delete(tempPath);

        log?.Invoke($"Downloading {resolution.Url} -> {destinationPath}");
        using (var response = await http.GetAsync(resolution.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(fs, ct);
        }

        if (!string.IsNullOrWhiteSpace(resolution.Hash))
        {
            var expected = resolution.Hash!.ToLowerInvariant();
            var actual = resolution.HashAlgorithm.Equals("SHA512", StringComparison.OrdinalIgnoreCase)
                ? await ComputeSha512HexAsync(tempPath, ct)
                : await CryptoUtils.ComputeSha256HexAsync(tempPath, ct);

            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                File.Delete(tempPath);
                throw new InvalidOperationException(
                    $"{resolution.HashAlgorithm} mismatch for {resolution.Url}. Expected {expected}, got {actual}.");
            }
            log?.Invoke($"Verified {resolution.HashAlgorithm}: {actual}");
        }
        else
        {
            log?.Invoke($"No vendor hash published for {resolution.Url}; relying on HTTPS trust ({resolution.TrustNote}).");
        }

        if (File.Exists(destinationPath)) File.Delete(destinationPath);
        File.Move(tempPath, destinationPath);

        var sha256 = await CryptoUtils.ComputeSha256HexAsync(destinationPath, ct);
        log?.Invoke($"Observed SHA-256: {sha256}");
        return sha256;
    }

    /// <summary>Async SHA-512 hex digest of a file.</summary>
    public static async Task<string> ComputeSha512HexAsync(string filePath, CancellationToken ct = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using var sha = SHA512.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
