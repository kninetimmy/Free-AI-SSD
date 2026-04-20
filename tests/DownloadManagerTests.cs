using System.Net;
using System.Security.Cryptography;
using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

public sealed class DownloadManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public DownloadManagerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Dest(string name) => Path.Combine(_dir, name);

    private static (DownloadManager manager, byte[] payload, string sha256) MakeManager(string fileName)
    {
        var payload = new byte[64];
        Random.Shared.NextBytes(payload);

        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(payload)).ToLowerInvariant();

        var handler = new FakeHttpHandler(payload);
        var manager = new DownloadManager(new HttpClient(handler));
        return (manager, payload, hash);
    }

    [Fact]
    public async Task DownloadFileWithResumeAsync_CorrectSha_WritesDestinationAndCleansTemp()
    {
        var (manager, payload, sha256) = MakeManager("file.bin");
        var dest = Dest("file.bin");
        var temp = dest + ".part";

        await manager.DownloadFileWithResumeAsync(new DownloadRequest("http://fake/file.bin", dest, sha256));

        Assert.True(File.Exists(dest));
        Assert.Equal(payload, File.ReadAllBytes(dest));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public async Task DownloadFileWithResumeAsync_WrongSha_ThrowsAndLeavesNoDestination()
    {
        var (manager, _, _) = MakeManager("file.bin");
        var dest = Dest("file.bin");
        var temp = dest + ".part";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.DownloadFileWithResumeAsync(new DownloadRequest("http://fake/file.bin", dest, "0000000000000000000000000000000000000000000000000000000000000000")));

        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public async Task DownloadFileWithResumeAsync_NoSha_WritesDestinationWithoutVerifying()
    {
        var (manager, payload, _) = MakeManager("file.bin");
        var dest = Dest("file.bin");

        await manager.DownloadFileWithResumeAsync(new DownloadRequest("http://fake/file.bin", dest));

        Assert.True(File.Exists(dest));
        Assert.Equal(payload, File.ReadAllBytes(dest));
    }

    private sealed class FakeHttpHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            return Task.FromResult(response);
        }
    }
}
