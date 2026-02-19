using FreeAiSsd.PrepApp;

namespace FreeAiSsd.Tests;

/// <summary>
/// Tests for ModelOperations — the OCI manifest parser and model blob resolver.
/// Covers the layer selection priority logic (media type → largest size → last layer),
/// model blob resolution from the Ollama manifest directory structure, and
/// argument safety for the Ollama CLI.
/// </summary>
public sealed class ModelOperationsTests
{
    /// <summary>
    /// Priority 1: When a layer has "model" in its media type, it should be
    /// selected over other layers regardless of size.
    /// </summary>
    [Fact]
    public void TrySelectModelLayerDigest_PrefersModelMediaTypeLayer()
    {
        var manifest = """
        {
          "schemaVersion": 2,
          "config": { "mediaType": "application/vnd.oci.image.config.v1+json", "digest": "sha256:aaaaaaaa", "size": 123 },
          "layers": [
            { "mediaType": "application/vnd.ollama.image.layer.template", "digest": "sha256:bbbbbbbb", "size": 10 },
            { "mediaType": "application/vnd.ollama.image.layer.model", "digest": "sha256:cccccccc", "size": 20 },
            { "mediaType": "application/vnd.ollama.image.layer.params", "digest": "sha256:dddddddd", "size": 30 }
          ]
        }
        """;

        var ok = ModelOperations.TrySelectModelLayerDigest(manifest, out var digest);

        Assert.True(ok);
        Assert.Equal("sha256:cccccccc", digest);
    }

    /// <summary>
    /// Priority 2: When no layer has a "model" media type, fall back to the
    /// largest layer by size (the model weights are always the biggest blob).
    /// </summary>
    [Fact]
    public void TrySelectModelLayerDigest_FallsBackToLargestLayer()
    {
        var manifest = """
        {
          "schemaVersion": 2,
          "config": { "digest": "sha256:aaaaaaaa", "size": 1 },
          "layers": [
            { "mediaType": "application/vnd.oci.image.layer.v1.tar", "digest": "sha256:bbbbbbbb", "size": 1024 },
            { "mediaType": "application/vnd.oci.image.layer.v1.tar", "digest": "sha256:cccccccc", "size": 4096 }
          ]
        }
        """;

        var ok = ModelOperations.TrySelectModelLayerDigest(manifest, out var digest);

        Assert.True(ok);
        Assert.Equal("sha256:cccccccc", digest);
    }

    /// <summary>
    /// Priority 3: When sizes are not available, fall back to the last layer
    /// in the array (a reasonable heuristic for older manifest formats).
    /// </summary>
    [Fact]
    public void TrySelectModelLayerDigest_UsesLastLayerWhenSizesMissing()
    {
        var manifest = """
        {
          "schemaVersion": 2,
          "layers": [
            { "mediaType": "application/vnd.oci.image.layer.v1.tar", "digest": "sha256:bbbbbbbb" },
            { "mediaType": "application/vnd.oci.image.layer.v1.tar", "digest": "sha256:cccccccc" }
          ]
        }
        """;

        var ok = ModelOperations.TrySelectModelLayerDigest(manifest, out var digest);

        Assert.True(ok);
        Assert.Equal("sha256:cccccccc", digest);
    }

    /// <summary>
    /// Integration test: creates an on-disk manifest + blobs directory structure
    /// and verifies that FindModelBlobForModel resolves the correct model blob
    /// (not the config blob) based on the manifest's layer media types.
    /// </summary>
    [Fact]
    public void FindModelBlobForModel_PicksLayerDigestNotConfigDigest()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"freeaissd-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var manifestDir = Path.Combine(tempRoot, "manifests", "registry.ollama.ai", "library", "llama3");
            Directory.CreateDirectory(manifestDir);

            var manifestPath = Path.Combine(manifestDir, "8b");
            File.WriteAllText(manifestPath, """
            {
              "schemaVersion": 2,
              "config": { "digest": "sha256:aaaaaaaa", "size": 123 },
              "layers": [
                { "mediaType": "application/vnd.oci.image.layer.v1.tar", "digest": "sha256:bbbbbbbb", "size": 10 },
                { "mediaType": "application/vnd.ollama.image.layer.model", "digest": "sha256:cccccccc", "size": 20 }
              ]
            }
            """);

            var blobsDir = Path.Combine(tempRoot, "blobs");
            Directory.CreateDirectory(blobsDir);
            var modelBlob = Path.Combine(blobsDir, "sha256-cccccccc");
            var configBlob = Path.Combine(blobsDir, "sha256-aaaaaaaa");
            File.WriteAllText(modelBlob, "model");
            File.WriteAllText(configBlob, "config");

            var result = ModelOperations.FindModelBlobForModel(tempRoot, "llama3:8b");

            Assert.Equal(modelBlob, result);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that BuildOllamaArgs keeps model tags as a single argument element
    /// (using ArgumentList, not string concatenation) to prevent shell injection
    /// with specially-crafted model names.
    /// </summary>
    [Theory]
    [InlineData("llama3:8b")]
    [InlineData("weird tag:latest")]
    [InlineData("\"quoted\":latest")]
    [InlineData("-leading-dash:1")]
    public void BuildOllamaArgs_KeepsTagAsSingleArgument(string modelTag)
    {
        var args = ModelOperations.BuildOllamaArgs("pull", modelTag);

        Assert.Equal(2, args.Count);
        Assert.Equal("pull", args[0]);
        Assert.Equal(modelTag, args[1]);
    }
}
