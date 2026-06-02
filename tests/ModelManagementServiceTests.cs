using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using FreeAiSsd.Runner.Services;
using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

/// <summary>
/// MAC33: pins the disk-truth swap in <see cref="ModelManagementService"/> so
/// the v1.3.10 Mac field-test scenario (encrypted config + model on disk →
/// empty picker) stops returning empty lists. Mac sidecar can't write back
/// to the encrypted config after pulls, so config.Models is unreliable.
/// </summary>
public sealed class ModelManagementServiceTests
{
    [Fact]
    public void GetInstalledModelNames_DiskTruth_ReturnsModelsOnDisk_WhenConfigEmpty()
    {
        // The MAC33 field-bug pin: encrypted config has no Models[] entry,
        // disk has a successfully-pulled model. Disk truth wins.
        using var tempRoot = new TempRoot();
        var (modelTag, _) = WriteContentAddressedModelOnDisk(tempRoot.Path, "llama3.2:1b", "fake-bytes");

        using var http = new HttpClient(new StubHandler());
        var service = new ModelManagementService(http, tempRoot.Path);
        var emptyConfig = new PortableConfig();

        var names = service.GetInstalledModelNames(emptyConfig);

        Assert.Contains(modelTag, names);
        Assert.Single(names);
    }

    [Fact]
    public void GetInstalledModelNames_DiskTruth_ReturnsMultipleModelsAlphabetically()
    {
        using var tempRoot = new TempRoot();
        WriteContentAddressedModelOnDisk(tempRoot.Path, "llama3.2:1b", "bytes-a");
        WriteContentAddressedModelOnDisk(tempRoot.Path, "phi3:mini", "bytes-b");
        WriteContentAddressedModelOnDisk(tempRoot.Path, "gemma:2b", "bytes-c");

        using var http = new HttpClient(new StubHandler());
        var service = new ModelManagementService(http, tempRoot.Path);

        var names = service.GetInstalledModelNames(new PortableConfig());

        Assert.Equal(new[] { "gemma:2b", "llama3.2:1b", "phi3:mini" }, names);
    }

    [Fact]
    public void GetInstalledModelNames_NoModelsOnDisk_ReturnsEmpty()
    {
        using var tempRoot = new TempRoot();
        // Empty SSD: no models/manifests directory at all.
        using var http = new HttpClient(new StubHandler());
        var service = new ModelManagementService(http, tempRoot.Path);

        var names = service.GetInstalledModelNames(new PortableConfig());

        Assert.Empty(names);
    }

    [Fact]
    public void GetInstalledModelNames_DiskTruthSupersedesConfig()
    {
        // Config has a stale "Installed" entry for a model whose blob was
        // never actually pulled. Disk wins — return only what's on disk.
        using var tempRoot = new TempRoot();
        var (realModel, _) = WriteContentAddressedModelOnDisk(tempRoot.Path, "llama3.2:1b", "real-bytes");

        var staleConfig = new PortableConfig
        {
            Models = new List<ModelConfigEntry>
            {
                new() { Name = "phantom-model:99b", Status = ModelInstallStatus.Installed }
            }
        };

        using var http = new HttpClient(new StubHandler());
        var service = new ModelManagementService(http, tempRoot.Path);

        var names = service.GetInstalledModelNames(staleConfig);

        Assert.Contains(realModel, names);
        Assert.DoesNotContain("phantom-model:99b", names);
    }

    [Fact]
    public void GetModelSizingWarnings_DiskTruth_PicksUpModelOnDisk()
    {
        // Sizing warnings were silent on Mac pre-MAC33 because config.Models
        // was empty. After the swap, disk-discovered models flow through.
        using var tempRoot = new TempRoot();
        WriteContentAddressedModelOnDisk(tempRoot.Path, "llama3:70b", "huge-model-bytes");

        using var http = new HttpClient(new StubHandler());
        var probe = new TestSystemResourceProbe { TotalRamGb = 4, GpuVramGb = 0 };
        var service = new ModelManagementService(http, probe, tempRoot.Path);

        var warnings = service.GetModelSizingWarnings(new PortableConfig());

        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, w => w.StartsWith("llama3:70b:", StringComparison.Ordinal));
    }

    [Fact]
    public void IsEmbeddingModelInstalled_BareConfigName_MatchesLatestTagOnDisk()
    {
        // Disk emits "nomic-embed-text:latest"; the PortableConfig default is the
        // bare "nomic-embed-text". Tag normalization must treat them as equal.
        using var tempRoot = new TempRoot();
        WriteContentAddressedModelOnDisk(tempRoot.Path, "nomic-embed-text:latest", "embed-bytes");

        using var http = new HttpClient(new StubHandler());
        var service = new ModelManagementService(http, tempRoot.Path);

        Assert.True(service.IsEmbeddingModelInstalled(new PortableConfig()));
    }

    [Fact]
    public void IsEmbeddingModelInstalled_CaseInsensitiveMatch()
    {
        using var tempRoot = new TempRoot();
        WriteContentAddressedModelOnDisk(tempRoot.Path, "nomic-embed-text:latest", "embed-bytes");

        using var http = new HttpClient(new StubHandler());
        var service = new ModelManagementService(http, tempRoot.Path);
        var config = new PortableConfig { EmbeddingModelName = "Nomic-Embed-Text" };

        Assert.True(service.IsEmbeddingModelInstalled(config));
    }

    [Fact]
    public void IsEmbeddingModelInstalled_ModelAbsent_ReturnsFalse()
    {
        using var tempRoot = new TempRoot();
        WriteContentAddressedModelOnDisk(tempRoot.Path, "llama3.2:1b", "chat-bytes");

        using var http = new HttpClient(new StubHandler());
        var service = new ModelManagementService(http, tempRoot.Path);

        Assert.False(service.IsEmbeddingModelInstalled(new PortableConfig()));
    }

    [Fact]
    public void IsEmbeddingModelInstalled_BlankConfigName_ReturnsFalse()
    {
        // A model is on disk, but no embedder is configured — nothing to match.
        using var tempRoot = new TempRoot();
        WriteContentAddressedModelOnDisk(tempRoot.Path, "nomic-embed-text:latest", "embed-bytes");

        using var http = new HttpClient(new StubHandler());
        var service = new ModelManagementService(http, tempRoot.Path);
        var config = new PortableConfig { EmbeddingModelName = "  " };

        Assert.False(service.IsEmbeddingModelInstalled(config));
    }

    private static (string ModelTag, string ContentSha) WriteContentAddressedModelOnDisk(string root, string modelTag, string blobBody)
    {
        var modelsDir = Path.Combine(root, SsdLayout.Models);
        var blobsDir = Path.Combine(modelsDir, "blobs");
        Directory.CreateDirectory(blobsDir);

        var bodyBytes = System.Text.Encoding.UTF8.GetBytes(blobBody);
        var sha = Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLowerInvariant();
        var blobName = $"sha256-{sha}";
        File.WriteAllBytes(Path.Combine(blobsDir, blobName), bodyBytes);

        var (name, tag) = SplitModelTag(modelTag);
        var manifestDir = Path.Combine(modelsDir, "manifests", "registry.ollama.ai", "library", name);
        Directory.CreateDirectory(manifestDir);
        var manifest = new
        {
            schemaVersion = 2,
            config = new { mediaType = "application/vnd.ollama.image.config.v1+json", digest = "sha256:0000", size = 1 },
            layers = new[]
            {
                new { mediaType = "application/vnd.ollama.image.layer.model", digest = $"sha256:{sha}", size = (long)bodyBytes.Length }
            }
        };
        File.WriteAllText(Path.Combine(manifestDir, tag), JsonSerializer.Serialize(manifest));

        return (modelTag, sha);
    }

    private static (string Name, string Tag) SplitModelTag(string modelTag)
    {
        var colon = modelTag.LastIndexOf(':');
        return (modelTag[..colon], modelTag[(colon + 1)..]);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class TestSystemResourceProbe : ISystemResourceProbe
    {
        public int? TotalRamGb { get; set; }
        public int? GpuVramGb { get; set; }

        public int? GetTotalSystemRamGb() => TotalRamGb;
        public int? GetGpuVramGb() => GpuVramGb;
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; }

        public TempRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"freeaissd-mmsvc-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
