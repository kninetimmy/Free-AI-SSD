using FreeAiSsd.Shared.Prereqs;

namespace FreeAiSsd.Tests;

/// <summary>
/// Parser-only coverage for <see cref="PrereqResolver"/>. These tests never
/// touch the network — they exercise the exact JSON / sha256sum.txt shapes
/// that Microsoft and GitHub publish, plus failure modes that the fail-closed
/// downloader depends on.
/// </summary>
public sealed class PrereqResolverTests
{
    [Fact]
    public void ParseDotnet8DesktopX64_HappyPath_ReturnsLatestStableWithSha512()
    {
        // Minimal shape modeled on the real
        // https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json
        const string json = """
        {
          "channel-version": "8.0",
          "latest-release": "8.0.18",
          "releases": [
            {
              "release-version": "8.0.17",
              "windowsdesktop": { "files": [] }
            },
            {
              "release-version": "8.0.18",
              "windowsdesktop": {
                "files": [
                  {
                    "name": "windowsdesktop-runtime-8.0.18-linux-x64.tar.gz",
                    "rid": "linux-x64",
                    "url": "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.18/windowsdesktop-runtime-8.0.18-linux-x64.tar.gz",
                    "hash": "aaaa"
                  },
                  {
                    "name": "windowsdesktop-runtime-8.0.18-win-x64.exe",
                    "rid": "win-x64",
                    "url": "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.18/windowsdesktop-runtime-8.0.18-win-x64.exe",
                    "hash": "ABCDEF0123456789"
                  }
                ]
              }
            }
          ]
        }
        """;

        var resolution = PrereqResolver.ParseDotnet8DesktopX64(json);

        Assert.Equal("8.0.18", resolution.Version);
        Assert.Equal(
            "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.18/windowsdesktop-runtime-8.0.18-win-x64.exe",
            resolution.Url);
        Assert.Equal("abcdef0123456789", resolution.Hash); // lowercased
        Assert.Equal("SHA512", resolution.HashAlgorithm);
        Assert.Contains("releases.json", resolution.TrustNote);
    }

    [Fact]
    public void ParseDotnet8DesktopX64_RejectsPreviewLatestRelease()
    {
        const string json = """
        { "latest-release": "8.0.19-preview.1", "releases": [] }
        """;

        var ex = Assert.Throws<InvalidOperationException>(
            () => PrereqResolver.ParseDotnet8DesktopX64(json));
        Assert.Contains("preview", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseDotnet8DesktopX64_RejectsNonHttpsUrl()
    {
        const string json = """
        {
          "latest-release": "8.0.18",
          "releases": [
            {
              "release-version": "8.0.18",
              "windowsdesktop": {
                "files": [
                  {
                    "name": "windowsdesktop-runtime-8.0.18-win-x64.exe",
                    "rid": "win-x64",
                    "url": "http://builds.dotnet.microsoft.com/insecure.exe",
                    "hash": "deadbeef"
                  }
                ]
              }
            }
          ]
        }
        """;

        var ex = Assert.Throws<InvalidOperationException>(
            () => PrereqResolver.ParseDotnet8DesktopX64(json));
        Assert.Contains("HTTPS", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseDotnet8DesktopX64_RejectsMissingHash()
    {
        const string json = """
        {
          "latest-release": "8.0.18",
          "releases": [
            {
              "release-version": "8.0.18",
              "windowsdesktop": {
                "files": [
                  {
                    "name": "windowsdesktop-runtime-8.0.18-win-x64.exe",
                    "rid": "win-x64",
                    "url": "https://builds.dotnet.microsoft.com/x.exe"
                  }
                ]
              }
            }
          ]
        }
        """;

        Assert.Throws<InvalidOperationException>(
            () => PrereqResolver.ParseDotnet8DesktopX64(json));
    }

    [Fact]
    public void ParseDotnet8DesktopX64_RejectsNonDotnet8LatestRelease()
    {
        const string json = """
        { "latest-release": "9.0.0", "releases": [] }
        """;

        Assert.Throws<InvalidOperationException>(
            () => PrereqResolver.ParseDotnet8DesktopX64(json));
    }

    [Fact]
    public void ParseOllamaLatestRelease_PicksPreferredAssetAndShaSibling()
    {
        const string json = """
        {
          "tag_name": "v0.20.7",
          "assets": [
            {
              "name": "Ollama-darwin.zip",
              "browser_download_url": "https://github.com/ollama/ollama/releases/download/v0.20.7/Ollama-darwin.zip"
            },
            {
              "name": "ollama-linux-amd64",
              "browser_download_url": "https://github.com/ollama/ollama/releases/download/v0.20.7/ollama-linux-amd64"
            },
            {
              "name": "sha256sum.txt",
              "browser_download_url": "https://github.com/ollama/ollama/releases/download/v0.20.7/sha256sum.txt"
            }
          ]
        }
        """;

        var result = PrereqResolver.ParseOllamaLatestRelease(json,
            new[] { "Ollama-darwin.zip", "ollama-darwin.zip" });

        Assert.Equal("v0.20.7", result.Tag);
        Assert.Equal("Ollama-darwin.zip", result.AssetName);
        Assert.StartsWith("https://", result.AssetUrl);
        Assert.EndsWith("sha256sum.txt", result.ShaAssetUrl);
    }

    [Fact]
    public void ParseOllamaLatestRelease_FallsBackToLowercaseAssetName()
    {
        const string json = """
        {
          "tag_name": "v0.19.0",
          "assets": [
            {
              "name": "ollama-darwin.zip",
              "browser_download_url": "https://github.com/ollama/ollama/releases/download/v0.19.0/ollama-darwin.zip"
            },
            {
              "name": "sha256sum.txt",
              "browser_download_url": "https://github.com/ollama/ollama/releases/download/v0.19.0/sha256sum.txt"
            }
          ]
        }
        """;

        var result = PrereqResolver.ParseOllamaLatestRelease(json,
            new[] { "Ollama-darwin.zip", "ollama-darwin.zip" });

        Assert.Equal("ollama-darwin.zip", result.AssetName);
    }

    [Fact]
    public void ParseOllamaLatestRelease_FailsWhenShaSumAssetMissing()
    {
        const string json = """
        {
          "tag_name": "v0.20.7",
          "assets": [
            {
              "name": "Ollama-darwin.zip",
              "browser_download_url": "https://github.com/ollama/ollama/releases/download/v0.20.7/Ollama-darwin.zip"
            }
          ]
        }
        """;

        Assert.Throws<InvalidOperationException>(
            () => PrereqResolver.ParseOllamaLatestRelease(json, new[] { "Ollama-darwin.zip" }));
    }

    [Fact]
    public void ParseSha256SumForFile_HandlesDotSlashAndStarPrefixes()
    {
        var text = string.Join('\n', new[]
        {
            "# comment line",
            "74670dafa053a48aef8c7666100cf91f2a3c84fe88cee1a8293e7a7cb3442eea  ./Ollama-darwin.zip",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  *ollama-linux-amd64",
        });

        Assert.Equal(
            "74670dafa053a48aef8c7666100cf91f2a3c84fe88cee1a8293e7a7cb3442eea",
            PrereqResolver.ParseSha256SumForFile(text, "Ollama-darwin.zip"));
        Assert.Equal(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            PrereqResolver.ParseSha256SumForFile(text, "ollama-linux-amd64"));
    }

    [Fact]
    public void ParseSha256SumForFile_ThrowsWhenFilenameMissing()
    {
        var text = "deadbeef  other.zip";
        Assert.Throws<InvalidOperationException>(
            () => PrereqResolver.ParseSha256SumForFile(text, "missing.zip"));
    }

    [Fact]
    public void ParseSha256SumForFile_RejectsShortHash()
    {
        // A "hash" that is the right position but wrong length must be rejected so
        // a mangled sha256sum.txt can never slip a weaker check past the resolver.
        var text = "deadbeef  Ollama-darwin.zip";
        Assert.Throws<InvalidOperationException>(
            () => PrereqResolver.ParseSha256SumForFile(text, "Ollama-darwin.zip"));
    }

    [Fact]
    public void ResolveVcRedistX64_ReturnsHttpsPermalinkWithNoHash()
    {
        var r = PrereqResolver.ResolveVcRedistX64();
        Assert.StartsWith("https://", r.Url);
        Assert.Equal("https://aka.ms/vs/17/release/vc_redist.x64.exe", r.Url);
        Assert.Null(r.Hash);
        Assert.Contains("HTTPS", r.TrustNote, StringComparison.OrdinalIgnoreCase);
    }
}
