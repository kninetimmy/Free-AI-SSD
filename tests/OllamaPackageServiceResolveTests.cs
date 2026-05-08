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
