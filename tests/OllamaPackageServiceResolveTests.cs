using FreeAiSsd.PrepApp.Services;

namespace FreeAiSsd.Tests;

public sealed class OllamaPackageServiceResolveTests
{
    [Fact]
    public void GetOllamaFileName_MatchesRuntimeOs()
    {
        var expected = OperatingSystem.IsWindows() ? "ollama.exe" : "ollama";
        Assert.Equal(expected, OllamaPackageService.GetOllamaFileName());
    }

    [Fact]
    public void ResolveOllamaExe_FindsWindowsBinary()
    {
        using var dir = new TempDir();
        var binaryPath = Path.Combine(dir.Path, "ollama.exe");
        File.WriteAllBytes(binaryPath, new byte[] { 0x4D, 0x5A });

        var resolved = OllamaPackageService.ResolveOllamaExe(dir.Path, "ollama.exe");

        Assert.Equal(binaryPath, resolved);
    }

    [Fact]
    public void ResolveOllamaExe_FindsMacBinary()
    {
        using var dir = new TempDir();
        var binaryPath = Path.Combine(dir.Path, "ollama");
        File.WriteAllBytes(binaryPath, new byte[] { 0xCF, 0xFA, 0xED, 0xFE });

        var resolved = OllamaPackageService.ResolveOllamaExe(dir.Path, "ollama");

        Assert.Equal(binaryPath, resolved);
    }

    [Fact]
    public void ResolveOllamaExe_MacFilenameIgnoresExeSibling()
    {
        // Field-bug pin: pre-MAC25 the resolver hardcoded "ollama.exe" so on a
        // Mac-staged drive it walked past the bare `ollama` binary and reported
        // "not found." This locks in that the Mac filename does not match a
        // stray sibling .exe should one ever land there.
        using var dir = new TempDir();
        var macPath = Path.Combine(dir.Path, "ollama");
        var winPath = Path.Combine(dir.Path, "ollama.exe");
        File.WriteAllBytes(macPath, new byte[] { 0xCF, 0xFA, 0xED, 0xFE });
        File.WriteAllBytes(winPath, new byte[] { 0x4D, 0x5A });

        var resolved = OllamaPackageService.ResolveOllamaExe(dir.Path, "ollama");

        Assert.Equal(macPath, resolved);
    }

    [Fact]
    public void ResolveOllamaExe_FindsBinaryInNestedSubdirectory()
    {
        using var dir = new TempDir();
        var nested = Path.Combine(dir.Path, "darwin-arm64");
        Directory.CreateDirectory(nested);
        var binaryPath = Path.Combine(nested, "ollama");
        File.WriteAllBytes(binaryPath, new byte[] { 0xCF, 0xFA, 0xED, 0xFE });

        var resolved = OllamaPackageService.ResolveOllamaExe(dir.Path, "ollama");

        Assert.Equal(binaryPath, resolved);
    }

    [Fact]
    public void ResolveOllamaExe_ReturnsNullWhenDirectoryMissing()
    {
        var ghost = Path.Combine(Path.GetTempPath(), $"freeaissd-ghost-{Guid.NewGuid():N}");

        var resolved = OllamaPackageService.ResolveOllamaExe(ghost, "ollama");

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveMacOllamaExe_FindsInnerResourcesBinary()
    {
        using var dir = new TempDir();
        var inner = Path.Combine(dir.Path, "Ollama.app", "Contents", "Resources", "ollama");
        Directory.CreateDirectory(Path.GetDirectoryName(inner)!);
        File.WriteAllBytes(inner, new byte[] { 0xCF, 0xFA, 0xED, 0xFE });

        var resolved = OllamaPackageService.ResolveMacOllamaExe(dir.Path);

        Assert.Equal(inner, resolved);
    }

    [Fact]
    public void ResolveMacOllamaExe_PrefersInnerBinaryOverShimAtBundleRoot()
    {
        // MAC26 field-bug pin: the upstream Mac Ollama distribution bundles a
        // 119 KB LaunchServices shim at the bundle root alongside Ollama.app.
        // The shim strips env vars (so OLLAMA_MODELS doesn't propagate) and
        // headlessly-launches the GUI app via a SIGKILL-prone path. Pre-MAC26
        // the resolver walked AllDirectories and FirstOrDefault'd whichever
        // file was found first — typically the shim. This test pins that the
        // Mac resolver returns the inner self-contained server even when a
        // sibling at the bundle root would also match the bare 'ollama' name.
        using var dir = new TempDir();
        var shim = Path.Combine(dir.Path, "ollama");
        var inner = Path.Combine(dir.Path, "Ollama.app", "Contents", "Resources", "ollama");
        Directory.CreateDirectory(Path.GetDirectoryName(inner)!);
        File.WriteAllBytes(shim, new byte[] { 0xCF, 0xFA, 0xED, 0xFE });
        File.WriteAllBytes(inner, new byte[] { 0xCF, 0xFA, 0xED, 0xFE });

        var resolved = OllamaPackageService.ResolveMacOllamaExe(dir.Path);

        Assert.Equal(inner, resolved);
    }

    [Fact]
    public void ResolveMacOllamaExe_ReturnsNullWhenBundleMissing()
    {
        using var dir = new TempDir();

        var resolved = OllamaPackageService.ResolveMacOllamaExe(dir.Path);

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveMacOllamaExe_ReturnsNullWhenDirectoryMissing()
    {
        var ghost = Path.Combine(Path.GetTempPath(), $"freeaissd-mac-ghost-{Guid.NewGuid():N}");

        var resolved = OllamaPackageService.ResolveMacOllamaExe(ghost);

        Assert.Null(resolved);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"freeaissd-resolve-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
