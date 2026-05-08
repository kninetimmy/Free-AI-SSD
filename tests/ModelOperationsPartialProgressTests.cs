using FreeAiSsd.PrepApp;
using Xunit;

namespace FreeAiSsd.Tests;

/// <summary>
/// MAC31: pin <see cref="ModelOperations.EstimatePartialProgress"/>'s
/// behavior against synthesized SSD layouts.
///
/// The seed is what makes "Retry resumes from 43%" feel right after
/// the user cancels a slow pull — without it the progress display
/// reads only live <c>ollama pull</c> stdout, so retry shows 0%
/// + re-validation phase before climbing. Ollama IS resumable; we
/// just weren't surfacing the resume state.
/// </summary>
public sealed class ModelOperationsPartialProgressTests : IDisposable
{
    private readonly string _root;

    public ModelOperationsPartialProgressTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "freeaissd-mac31-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void EstimatePartialProgress_NoManifest_ReturnsZero()
    {
        var modelsRoot = Path.Combine(_root, "models");
        Directory.CreateDirectory(modelsRoot);

        var fraction = ModelOperations.EstimatePartialProgress(modelsRoot, "llama3.2:1b");

        Assert.Equal(0.0, fraction);
    }

    [Fact]
    public void EstimatePartialProgress_ModelsRootMissing_ReturnsZero()
    {
        var fraction = ModelOperations.EstimatePartialProgress(
            Path.Combine(_root, "does-not-exist"),
            "llama3.2:1b");
        Assert.Equal(0.0, fraction);
    }

    [Fact]
    public void EstimatePartialProgress_OnlyFullBlob_ReturnsOne()
    {
        var modelsRoot = SetupModelsRoot();
        var digest = WriteSyntheticManifest(modelsRoot, "llama3.2", "1b", layerSizes: new[] { 1000L });
        WriteFullBlob(modelsRoot, digest[0], 1000);

        var fraction = ModelOperations.EstimatePartialProgress(modelsRoot, "llama3.2:1b");

        Assert.Equal(1.0, fraction, precision: 5);
    }

    [Fact]
    public void EstimatePartialProgress_OnlyPartial_ReturnsFraction()
    {
        var modelsRoot = SetupModelsRoot();
        var digest = WriteSyntheticManifest(modelsRoot, "llama3.2", "1b", layerSizes: new[] { 1000L });
        WritePartialBlob(modelsRoot, digest[0], partialIndex: 0, sizeBytes: 430);

        var fraction = ModelOperations.EstimatePartialProgress(modelsRoot, "llama3.2:1b");

        Assert.Equal(0.43, fraction, precision: 5);
    }

    [Fact]
    public void EstimatePartialProgress_MixedFullAndPartial_SumsCorrectly()
    {
        var modelsRoot = SetupModelsRoot();
        var digests = WriteSyntheticManifest(modelsRoot, "llama3.2", "1b",
            layerSizes: new[] { 1000L, 2000L });
        WriteFullBlob(modelsRoot, digests[0], 1000);                    // layer 0: 100%
        WritePartialBlob(modelsRoot, digests[1], 0, sizeBytes: 1000);   // layer 1: 50%

        var fraction = ModelOperations.EstimatePartialProgress(modelsRoot, "llama3.2:1b");

        // (1000 + 1000) / (1000 + 2000) = 0.6667
        Assert.Equal(2000.0 / 3000.0, fraction, precision: 4);
    }

    [Fact]
    public void EstimatePartialProgress_MultiplePartialChunksPerLayer_SumsThenCaps()
    {
        // Ollama's parallel-chunk download writes one partial file per
        // chunk; pin that we sum them and cap at the layer's expected
        // size so a ragged retry can't overshoot 100%.
        var modelsRoot = SetupModelsRoot();
        var digest = WriteSyntheticManifest(modelsRoot, "llama3.2", "1b", layerSizes: new[] { 1000L });
        WritePartialBlob(modelsRoot, digest[0], partialIndex: 0, sizeBytes: 600);
        WritePartialBlob(modelsRoot, digest[0], partialIndex: 1, sizeBytes: 600);

        var fraction = ModelOperations.EstimatePartialProgress(modelsRoot, "llama3.2:1b");

        // 600+600=1200 sums beyond 1000; cap to layer size.
        Assert.Equal(1.0, fraction, precision: 5);
    }

    [Fact]
    public void EstimatePartialProgress_ManifestMalformed_ReturnsZero()
    {
        var modelsRoot = SetupModelsRoot();
        var manifestPath = Path.Combine(modelsRoot, "manifests", "registry.ollama.ai", "library", "llama3.2", "1b");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, "{ this is not valid JSON ");

        var fraction = ModelOperations.EstimatePartialProgress(modelsRoot, "llama3.2:1b");

        Assert.Equal(0.0, fraction);
    }

    [Fact]
    public void EstimatePartialProgress_BlobsDirMissing_ReturnsZero()
    {
        var modelsRoot = SetupModelsRoot();
        WriteSyntheticManifest(modelsRoot, "llama3.2", "1b", layerSizes: new[] { 1000L });
        // intentionally no blobs/ dir

        var fraction = ModelOperations.EstimatePartialProgress(modelsRoot, "llama3.2:1b");

        Assert.Equal(0.0, fraction);
    }

    [Fact]
    public void EstimatePartialProgress_PathTraversalTag_RefusedSafely()
    {
        // Hostile model tag must not coerce path construction into
        // reading manifests outside modelsRoot.
        var modelsRoot = SetupModelsRoot();

        var fraction = ModelOperations.EstimatePartialProgress(modelsRoot, "../../../etc/passwd:1b");

        Assert.Equal(0.0, fraction);
    }

    [Fact]
    public void EstimatePartialProgress_TagWithSlash_RefusedSafely()
    {
        var modelsRoot = SetupModelsRoot();

        var fraction = ModelOperations.EstimatePartialProgress(modelsRoot, "llama/secret:1b");

        Assert.Equal(0.0, fraction);
    }

    // --- Helpers ---------------------------------------------------------

    private string SetupModelsRoot()
    {
        var modelsRoot = Path.Combine(_root, "models");
        Directory.CreateDirectory(modelsRoot);
        return modelsRoot;
    }

    /// <summary>
    /// Writes a synthetic OCI-shaped manifest at
    /// <c>manifests/registry.ollama.ai/library/{model}/{tag}</c> with one
    /// layer per entry in <paramref name="layerSizes"/>. Returns the
    /// digests it generated (in layer order) so the test can write
    /// matching blob files.
    /// </summary>
    private static string[] WriteSyntheticManifest(string modelsRoot, string model, string tag, long[] layerSizes)
    {
        var dir = Path.Combine(modelsRoot, "manifests", "registry.ollama.ai", "library", model);
        Directory.CreateDirectory(dir);
        var manifestPath = Path.Combine(dir, tag);

        var digests = new string[layerSizes.Length];
        var layerJson = new System.Text.StringBuilder();
        for (var i = 0; i < layerSizes.Length; i++)
        {
            // Synthetic but realistic-shaped 64-char hex digest, unique per layer.
            digests[i] = "abcdef0123456789".PadRight(64, (char)('a' + i));
            if (i > 0) layerJson.Append(',');
            layerJson.Append("{\"digest\":\"sha256:").Append(digests[i]).Append("\",\"size\":").Append(layerSizes[i]).Append(",\"mediaType\":\"application/vnd.ollama.image.layer.model\"}");
        }
        File.WriteAllText(manifestPath, "{\"layers\":[" + layerJson + "]}");
        return digests;
    }

    private static void WriteFullBlob(string modelsRoot, string digest, long sizeBytes)
    {
        var blobsDir = Path.Combine(modelsRoot, "blobs");
        Directory.CreateDirectory(blobsDir);
        var path = Path.Combine(blobsDir, "sha256-" + digest);
        File.WriteAllBytes(path, new byte[sizeBytes]);
    }

    private static void WritePartialBlob(string modelsRoot, string digest, int partialIndex, long sizeBytes)
    {
        var blobsDir = Path.Combine(modelsRoot, "blobs");
        Directory.CreateDirectory(blobsDir);
        var path = Path.Combine(blobsDir, $"sha256-{digest}-partial-{partialIndex}");
        File.WriteAllBytes(path, new byte[sizeBytes]);
    }
}
