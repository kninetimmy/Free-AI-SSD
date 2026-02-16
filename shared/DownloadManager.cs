using System.Security.Cryptography;

namespace FreeAiSsd.Shared;

public sealed record DownloadRequest(string Url, string DestinationPath, string? Sha256 = null);

public sealed class DownloadProgress
{
    public long BytesReceived { get; init; }
    public long? TotalBytes { get; init; }
    public double Percent => TotalBytes is > 0 ? (double)BytesReceived / TotalBytes.Value * 100 : 0;
}

public sealed class DownloadManager
{
    private readonly HttpClient _httpClient;

    public DownloadManager(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task DownloadFileWithResumeAsync(DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);

        var tempPath = request.DestinationPath + ".part";
        var existingBytes = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, request.Url);
        if (existingBytes > 0)
        {
            httpRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingBytes, null);
        }

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        if (existingBytes > 0 && response.StatusCode == System.Net.HttpStatusCode.OK)
        {
            existingBytes = 0;
            File.Delete(tempPath);
        }

        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength + existingBytes;

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

        destination.Close();
        File.Move(tempPath, request.DestinationPath, overwrite: true);

        if (!string.IsNullOrWhiteSpace(request.Sha256))
        {
            VerifySha256(request.DestinationPath, request.Sha256!);
        }
    }

    public static void VerifySha256(string filePath, string expectedSha256)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        var actual = Convert.ToHexString(hash);
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"SHA256 mismatch for {filePath}. Expected {expectedSha256}, got {actual}.");
        }
    }
}
