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

    /// <summary>
    /// MAC35a: macOS auto-creates AppleDouble companion files (`._&lt;name&gt;`)
    /// alongside any file with extended attributes when written to a
    /// filesystem without native xattr support (exFAT, FAT32, SMB).
    /// Their first byte is the AppleDouble magic 0x00, so feeding one to
    /// JsonDocument.Parse throws "0x00 is an invalid start of a value"
    /// — which is what crashed readiness on the v1.3.15 mac field test.
    /// Discovery must skip them so callers never end up reading one as
    /// a manifest.
    /// </summary>
    [Fact]
    public void DiscoverModelsOnDisk_SkipsAppleDoubleSidecars()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"freeaissd-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var manifestDir = Path.Combine(tempRoot, "manifests", "registry.ollama.ai", "library", "qwen2.5");
            Directory.CreateDirectory(manifestDir);
            File.WriteAllText(Path.Combine(manifestDir, "7b"), "{\"schemaVersion\":2,\"layers\":[]}");
            File.WriteAllBytes(Path.Combine(manifestDir, "._7b"), new byte[] { 0x00, 0x05, 0x16, 0x07, 0x00, 0x02, 0x00, 0x00 });

            var discovered = ModelOperations.DiscoverModelsOnDisk(tempRoot);

            Assert.Contains("qwen2.5:7b", discovered);
            Assert.DoesNotContain("qwen2.5:._7b", discovered);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// MAC35a: <see cref="ModelOperations.FindModelBlobForModel"/> must
    /// resolve to the real manifest, not the AppleDouble sidecar, even
    /// if the directory enumeration happens to surface the sidecar
    /// first (`.` sorts before alphanumerics on some filesystems).
    /// </summary>
    [Fact]
    public void FindModelBlobForModel_SkipsAppleDoubleSidecar()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"freeaissd-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var manifestDir = Path.Combine(tempRoot, "manifests", "registry.ollama.ai", "library", "qwen2.5");
            Directory.CreateDirectory(manifestDir);
            File.WriteAllText(Path.Combine(manifestDir, "7b"), """
            {
              "schemaVersion": 2,
              "config": { "digest": "sha256:aaaaaaaa", "size": 1 },
              "layers": [
                { "mediaType": "application/vnd.ollama.image.layer.model", "digest": "sha256:cccccccc", "size": 20 }
              ]
            }
            """);
            File.WriteAllBytes(Path.Combine(manifestDir, "._7b"), new byte[] { 0x00, 0x05, 0x16, 0x07 });

            var blobsDir = Path.Combine(tempRoot, "blobs");
            Directory.CreateDirectory(blobsDir);
            var modelBlob = Path.Combine(blobsDir, "sha256-cccccccc");
            File.WriteAllText(modelBlob, "model");

            var result = ModelOperations.FindModelBlobForModel(tempRoot, "qwen2.5:7b");

            Assert.Equal(modelBlob, result);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// MAC35a: a malformed or non-JSON manifest must surface as
    /// <c>false</c> from <see cref="ModelOperations.TrySelectModelLayerDigest"/>
    /// rather than throwing — otherwise readiness aborts on the first
    /// corrupt file under <c>manifests/</c> and surfaces the C#
    /// JsonException to the user instead of a clean check failure.
    /// </summary>
    [Fact]
    public void TrySelectModelLayerDigest_ReturnsFalseOnCorruptJson()
    {
        // AppleDouble magic header — first byte 0x00 — the exact shape
        // that crashed readiness on the v1.3.15 field test.
        var corrupt = " garbage";
        var ok = ModelOperations.TrySelectModelLayerDigest(corrupt, out var digest);
        Assert.False(ok);
        Assert.Equal(string.Empty, digest);
    }

    /// <summary>
    /// 2026-05-11 HF pull regression: Ollama writes Hugging Face GGUF
    /// manifests under <c>manifests/hf.co/&lt;owner&gt;/&lt;repo&gt;/&lt;quant&gt;</c>,
    /// not the <c>registry.ollama.ai/library/</c> path used for stock
    /// Ollama models. Before this pin, <see cref="ModelOperations.FindModelBlobForModel"/>
    /// hardcoded the registry path and returned null for every HF tag,
    /// which surfaced as "Pull failed: Unable to locate model blob" in
    /// the sidecar log AFTER Ollama's NDJSON stream emitted
    /// <c>progress: success</c> — the user perceived this as "model
    /// downloaded fully then failed" because the HTTP pull succeeded
    /// but the post-pull hash step couldn't find the blob.
    /// </summary>
    [Fact]
    public void FindModelBlobForModel_ResolvesHuggingFaceManifestPath()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"freeaissd-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            // Path layout matches the on-disk shape Ollama writes for
            // HF tags. Owner casing and underscores in the repo name
            // are deliberate — both rejected by the prior allowlist.
            var manifestDir = Path.Combine(tempRoot, "manifests", "hf.co", "Andycurrent", "Gemma-3-1B-it_GGUF");
            Directory.CreateDirectory(manifestDir);
            File.WriteAllText(Path.Combine(manifestDir, "Q2_K"), """
            {
              "schemaVersion": 2,
              "config": { "digest": "sha256:aaaaaaaa", "size": 1 },
              "layers": [
                { "mediaType": "application/vnd.ollama.image.layer.model", "digest": "sha256:cccccccc", "size": 20 }
              ]
            }
            """);
            var blobsDir = Path.Combine(tempRoot, "blobs");
            Directory.CreateDirectory(blobsDir);
            var modelBlob = Path.Combine(blobsDir, "sha256-cccccccc");
            File.WriteAllText(modelBlob, "model");

            var result = ModelOperations.FindModelBlobForModel(tempRoot, "hf.co/Andycurrent/Gemma-3-1B-it_GGUF:Q2_K");

            Assert.Equal(modelBlob, result);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// 2026-05-11 HF pull regression: hostile HF tags that try to escape
    /// the <c>hf.co/&lt;owner&gt;/&lt;repo&gt;</c> shape must fail closed.
    /// The resolver enforces exactly two safe segments after the
    /// <c>hf.co/</c> prefix; anything else returns null (and callers
    /// surface "model blob not found" rather than reading from an
    /// arbitrary path).
    /// </summary>
    [Theory]
    [InlineData("hf.co/../../etc/passwd:Q4_K_M")]   // path traversal in owner slot
    [InlineData("hf.co/owner/../escape:Q4_K_M")]    // path traversal in repo slot
    [InlineData("hf.co/owner:Q4_K_M")]              // missing repo segment
    [InlineData("hf.co/owner/repo/extra:Q4_K_M")]   // too many path segments
    [InlineData("hf.co/owner/repo:bad/tag")]        // separator in tag
    public void TryResolveOllamaManifestPath_RefusesHostileHfTags(string modelTag)
    {
        var ok = ModelOperations.TryResolveOllamaManifestPath(
            modelTag, out var subdir, out var manifestTag);

        Assert.False(ok);
        Assert.Equal(string.Empty, subdir);
        Assert.Equal(string.Empty, manifestTag);
    }

    /// <summary>
    /// 2026-05-11 HF pull regression: standard ollama.com tags must
    /// continue to resolve under <c>registry.ollama.ai/library/</c>
    /// after the HF branch was added. Sanity pin so the helper's
    /// dispatch logic doesn't accidentally route non-HF tags into the
    /// HF subtree.
    /// </summary>
    [Fact]
    public void TryResolveOllamaManifestPath_RoutesStandardTagsToRegistryLibrary()
    {
        var ok = ModelOperations.TryResolveOllamaManifestPath(
            "llama3.2:1b", out var subdir, out var manifestTag);

        Assert.True(ok);
        Assert.Equal(Path.Combine("registry.ollama.ai", "library", "llama3.2"), subdir);
        Assert.Equal("1b", manifestTag);
    }

    /// <summary>
    /// 2026-05-11 HF pull regression: HF tags must route to the
    /// <c>hf.co/&lt;owner&gt;/&lt;repo&gt;</c> subtree with the quant as
    /// the manifest filename. This is the path that
    /// <see cref="OllamaModelStager.MergeToSsdAsync"/> and
    /// <see cref="ModelOperations.FindModelBlobForModel"/> both build
    /// from — getting the dispatch wrong is the root cause of the
    /// "model downloads then fails" symptom.
    /// </summary>
    [Fact]
    public void TryResolveOllamaManifestPath_RoutesHfTagsToHfSubtree()
    {
        var ok = ModelOperations.TryResolveOllamaManifestPath(
            "hf.co/Owner/Repo-GGUF:Q4_K_M", out var subdir, out var manifestTag);

        Assert.True(ok);
        Assert.Equal(Path.Combine("hf.co", "Owner", "Repo-GGUF"), subdir);
        Assert.Equal("Q4_K_M", manifestTag);
    }

    /// <summary>
    /// 2026-05-11 HF pull regression: <see cref="ModelOperations.DiscoverModelsOnDisk"/>
    /// previously reconstructed tag strings from the last two path
    /// segments, which dropped the <c>hf.co/&lt;owner&gt;/</c> prefix and
    /// reported HF models under just <c>&lt;repo&gt;:&lt;quant&gt;</c>. The
    /// picker then couldn't match installed HF rows against the
    /// catalog entries (which carry the full <c>hf.co/...</c> tag), so
    /// freshly-pulled HF models still showed as "not on disk". This
    /// pin verifies the discovery reports the full HF tag and that
    /// standard ollama.com tags continue to surface as <c>name:tag</c>.
    /// </summary>
    [Fact]
    public void DiscoverModelsOnDisk_ReconstructsHuggingFaceTagWithFullPrefix()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"freeaissd-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var hfDir = Path.Combine(tempRoot, "manifests", "hf.co", "Owner", "Repo-GGUF");
            Directory.CreateDirectory(hfDir);
            File.WriteAllText(Path.Combine(hfDir, "Q4_K_M"), "{\"schemaVersion\":2,\"layers\":[]}");

            var stdDir = Path.Combine(tempRoot, "manifests", "registry.ollama.ai", "library", "llama3.2");
            Directory.CreateDirectory(stdDir);
            File.WriteAllText(Path.Combine(stdDir, "1b"), "{\"schemaVersion\":2,\"layers\":[]}");

            var discovered = ModelOperations.DiscoverModelsOnDisk(tempRoot);

            Assert.Contains("hf.co/Owner/Repo-GGUF:Q4_K_M", discovered);
            Assert.Contains("llama3.2:1b", discovered);
            // Sanity: no entry under the stripped form that the prior
            // heuristic would have emitted.
            Assert.DoesNotContain("Repo-GGUF:Q4_K_M", discovered);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
