using FreeAiSsd.Shared.Prereqs;

namespace FreeAiSsd.Tests;

/// <summary>
/// Parser-only coverage for <see cref="PiperResolver"/>. These tests never
/// touch the network — they exercise the exact Hugging Face tree-API JSON
/// shape (real samples captured against rhasspy/piper-voices), plus the
/// failure modes that the fail-closed staging pipeline depends on.
/// </summary>
public sealed class PiperResolverTests
{
    private static PiperVoice TestVoice => PiperCatalog.DefaultVoice;

    [Fact]
    public void ParseVoiceTree_HappyPath_ReturnsLfsOidAndResolveUrls()
    {
        // Real shape returned by
        // https://huggingface.co/api/models/rhasspy/piper-voices/tree/main/en/en_US/amy/medium
        // (captured 2026-05-20; only the fields PiperResolver reads are kept).
        const string json = """
        [
          {"type":"directory","oid":"79595caf4c37981dc104f7bc9a6ce9c04b73aea5","size":0,
           "path":"en/en_US/amy/medium/samples"},
          {"type":"file","oid":"afaf80d8eb2304e99e5ac516c764e9f35ae624ba","size":281,
           "path":"en/en_US/amy/medium/MODEL_CARD"},
          {"type":"file","oid":"1d703b260d0732739ed941fead81f4514b91a79e","size":63201294,
           "lfs":{"oid":"b3a6e47b57b8c7fbe6a0ce2518161a50f59a9cdd8a50835c02cb02bdd6206c18",
                  "size":63201294,"pointerSize":133},
           "path":"en/en_US/amy/medium/en_US-amy-medium.onnx"},
          {"type":"file","oid":"5a1e0a2d94d3719de29a4771aa0e6c116552445f","size":4882,
           "path":"en/en_US/amy/medium/en_US-amy-medium.onnx.json"}
        ]
        """;

        var resolution = PiperResolver.ParseVoiceTree(json, TestVoice);

        Assert.Equal(TestVoice, resolution.Voice);
        Assert.Equal(
            "b3a6e47b57b8c7fbe6a0ce2518161a50f59a9cdd8a50835c02cb02bdd6206c18",
            resolution.OnnxSha256);
        Assert.Equal(63201294L, resolution.OnnxSizeBytes);
        Assert.Equal(
            "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/amy/medium/en_US-amy-medium.onnx",
            resolution.OnnxUrl);
        Assert.Equal(TestVoice.OnnxJsonSha256, resolution.OnnxJsonSha256);
        Assert.Equal(4882L, resolution.OnnxJsonSizeBytes);
        Assert.Equal(
            "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/amy/medium/en_US-amy-medium.onnx.json",
            resolution.OnnxJsonUrl);
        Assert.Contains("Hugging Face", resolution.TrustNote);
    }

    [Fact]
    public void ParseVoiceTree_OidIsLowercased()
    {
        const string json = """
        [
          {"type":"file","oid":"x","size":63201294,
           "lfs":{"oid":"B3A6E47B57B8C7FBE6A0CE2518161A50F59A9CDD8A50835C02CB02BDD6206C18",
                  "size":63201294,"pointerSize":133},
           "path":"en/en_US/amy/medium/en_US-amy-medium.onnx"},
          {"type":"file","oid":"y","size":4882,
           "path":"en/en_US/amy/medium/en_US-amy-medium.onnx.json"}
        ]
        """;

        var resolution = PiperResolver.ParseVoiceTree(json, TestVoice);
        Assert.Equal(
            "b3a6e47b57b8c7fbe6a0ce2518161a50f59a9cdd8a50835c02cb02bdd6206c18",
            resolution.OnnxSha256);
    }

    [Fact]
    public void ParseVoiceTree_MissingOnnx_Throws()
    {
        const string json = """
        [
          {"type":"file","oid":"y","size":4882,
           "path":"en/en_US/amy/medium/en_US-amy-medium.onnx.json"}
        ]
        """;

        var ex = Assert.Throws<InvalidOperationException>(
            () => PiperResolver.ParseVoiceTree(json, TestVoice));
        Assert.Contains("en_US-amy-medium.onnx", ex.Message);
    }

    [Fact]
    public void ParseVoiceTree_MissingJson_Throws()
    {
        const string json = """
        [
          {"type":"file","oid":"x","size":63201294,
           "lfs":{"oid":"b3a6e47b57b8c7fbe6a0ce2518161a50f59a9cdd8a50835c02cb02bdd6206c18",
                  "size":63201294,"pointerSize":133},
           "path":"en/en_US/amy/medium/en_US-amy-medium.onnx"}
        ]
        """;

        var ex = Assert.Throws<InvalidOperationException>(
            () => PiperResolver.ParseVoiceTree(json, TestVoice));
        Assert.Contains("en_US-amy-medium.onnx.json", ex.Message);
    }

    [Fact]
    public void ParseVoiceTree_OnnxWithoutLfsPointer_Throws()
    {
        // A non-LFS file is a sign the upstream voice was re-uploaded as a
        // plain blob — we refuse to install without a verifiable hash.
        const string json = """
        [
          {"type":"file","oid":"plain-blob-sha1","size":63201294,
           "path":"en/en_US/amy/medium/en_US-amy-medium.onnx"},
          {"type":"file","oid":"y","size":4882,
           "path":"en/en_US/amy/medium/en_US-amy-medium.onnx.json"}
        ]
        """;

        var ex = Assert.Throws<InvalidOperationException>(
            () => PiperResolver.ParseVoiceTree(json, TestVoice));
        Assert.Contains("LFS", ex.Message);
    }

    [Fact]
    public void ParseVoiceTree_BadOidLength_Throws()
    {
        const string json = """
        [
          {"type":"file","oid":"x","size":63201294,
           "lfs":{"oid":"abc","size":63201294,"pointerSize":133},
           "path":"en/en_US/amy/medium/en_US-amy-medium.onnx"},
          {"type":"file","oid":"y","size":4882,
           "path":"en/en_US/amy/medium/en_US-amy-medium.onnx.json"}
        ]
        """;

        var ex = Assert.Throws<InvalidOperationException>(
            () => PiperResolver.ParseVoiceTree(json, TestVoice));
        Assert.Contains("wrong length", ex.Message);
    }

    [Fact]
    public void ParseVoiceTree_NotJsonArray_Throws()
    {
        const string json = """{"error":"not found"}""";

        var ex = Assert.Throws<InvalidOperationException>(
            () => PiperResolver.ParseVoiceTree(json, TestVoice));
        Assert.Contains("not a JSON array", ex.Message);
    }

    [Fact]
    public void BuildTreeApiUrl_UsesRepoAndPath()
    {
        var url = PiperResolver.BuildTreeApiUrl(TestVoice);
        Assert.Equal(
            "https://huggingface.co/api/models/rhasspy/piper-voices/tree/main/en/en_US/amy/medium",
            url);
    }

    [Fact]
    public void BuildFileResolveUrl_FollowsLfsPointer()
    {
        var url = PiperResolver.BuildFileResolveUrl(TestVoice, "en_US-amy-medium.onnx");
        Assert.Equal(
            "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/amy/medium/en_US-amy-medium.onnx",
            url);
    }
}
