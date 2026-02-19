using System.Security.Cryptography;

namespace FreeAiSsd.Shared;

/// <summary>
/// Describes a file to download, including the source URL, local destination path,
/// and an optional SHA-256 hash for post-download integrity verification.
/// </summary>
public sealed record DownloadRequest(string Url, string DestinationPath, string? Sha256 = null);

/// <summary>
/// Reports progress of an in-flight download, including bytes transferred
/// and the total expected size (if known from Content-Length).
/// </summary>
public sealed class DownloadProgress
{
    public long BytesReceived { get; init; }
    public long? TotalBytes { get; init; }

    /// <summary>
    /// Calculates percentage completion. Returns 0 if total size is unknown.
    /// </summary>
    public double Percent => TotalBytes is > 0 ? (double)BytesReceived / TotalBytes.Value * 100 : 0;
}

/// <summary>
/// Handles file downloads with HTTP range-based resume support and optional
/// SHA-256 verification after completion. Uses a temporary ".part" file
/// during download to prevent partial files from being mistaken as complete.
/// </summary>
public sealed class DownloadManager
{
    private readonly HttpClient _httpClient;

    public DownloadManager(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Downloads a file with automatic resume support. If a partial ".part" file
    /// exists from a previous attempt, the download resumes from where it left off
    /// using HTTP Range headers. After completion, the file is atomically moved
    /// to the final destination path.
    /// </summary>
    /// <param name="request">Download parameters including URL, destination, and optional hash.</param>
    /// <param name="progress">Optional progress reporter for UI updates.</param>
    /// <param name="ct">Cancellation token to abort the download.</param>
    public async Task DownloadFileWithResumeAsync(DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        // Ensure the destination directory exists before starting.
        Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);

        var tempPath = request.DestinationPath + ".part";
        var existingBytes = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;

        // If we have a partial download, request only the remaining bytes.
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, request.Url);
        if (existingBytes > 0)
        {
            httpRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingBytes, null);
        }

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        // If the server responds with 200 (full content) instead of 206 (partial),
        // it doesn't support resume — restart the download from scratch.
        if (existingBytes > 0 && response.StatusCode == System.Net.HttpStatusCode.OK)
        {
            existingBytes = 0;
            File.Delete(tempPath);
        }

        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength + existingBytes;

        // Stream the response body to disk, appending to the partial file.
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var destination = new FileStream(tempPath, FileMode.Append, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long totalRead = existingBytes;
        int read;

        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            totalRead += read;
            progress?.Report(new DownloadProgress { BytesReceived = totalRead, TotalBytes = totalBytes });
        }

        // Close the file stream before moving to avoid sharing violations.
        destination.Close();
        File.Move(tempPath, request.DestinationPath, overwrite: true);

        // If a SHA-256 hash was provided, verify the downloaded file's integrity.
        if (!string.IsNullOrWhiteSpace(request.Sha256))
        {
            VerifySha256(request.DestinationPath, request.Sha256!);
        }
    }

    /// <summary>
    /// Compares the SHA-256 hash of a file against an expected value.
    /// Throws InvalidOperationException on mismatch.
    /// </summary>
    public static void VerifySha256(string filePath, string expectedSha256)
    {
        var actual = ComputeSha256(filePath);
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"SHA256 mismatch for {filePath}. Expected {expectedSha256}, got {actual}.");
        }
    }

    /// <summary>
    /// Computes the SHA-256 hash of a file and returns it as an uppercase hex string.
    /// </summary>
    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }
}
