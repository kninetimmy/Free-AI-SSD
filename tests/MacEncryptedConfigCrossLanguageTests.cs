using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

/// <summary>
/// Pins the on-disk format of the encrypted portable-config so the Swift
/// `mac-runner` port (mac-runner/Sources/SsdEncryption.swift) and the C#
/// <see cref="SsdEncryption"/> implementation cannot drift apart.
///
/// The fixture under tests/Fixtures/MacEncryptedConfig/csharp-encrypted/ is
/// produced by the Swift test binary (run with the `write-fixture` subcommand)
/// and committed to the repo. These C# tests:
///
///   1. Assert the wire JSON uses the lowercase camelCase field names the Swift
///      Codable types expect.
///   2. Decrypt the Swift-produced fixture using the production C# unlock
///      path, proving Swift→C# format compatibility byte-for-byte.
///   3. Encrypt a known config in C#, hand the encrypted output to a tiny
///      in-test re-deserializer, and check the wire shape matches what
///      Swift's JSONDecoder will accept.
///
/// If a deliberate format change ships, the fixture must be regenerated AND a
/// dated decision recorded in agent_docs/project_decisions.md. The fixture
/// password and expected plaintext are documented in the README.md inside
/// the fixture directory.
/// </summary>
public sealed class MacEncryptedConfigCrossLanguageTests
{
    private const string FixturePassword = "mac5-cross-lang-fixture-pw";
    private const int    FixtureOllamaPort = 13577;
    private const string FixtureFirstModelName = "llama3.2:3b";

    [Fact]
    public void CSharpUnlock_OnSwiftProducedFixture_RecoversExpectedPlaintext()
    {
        var fixtureRoot = SwiftFixtureRoot();
        Assert.True(Directory.Exists(fixtureRoot),
            $"Swift fixture missing — regenerate with `swiftc … && /tmp/ssd-encryption-tests write-fixture {fixtureRoot}`. See {Path.Combine(fixtureRoot, "README.md")}.");

        var unlocked = SsdEncryption.TryUnlockPortableConfig(
            fixtureRoot, FixturePassword, out var config, out var error);

        Assert.True(unlocked, $"Swift fixture failed to unlock: {error}");
        Assert.NotNull(config);
        Assert.Equal(FixtureOllamaPort, config!.OllamaPort);
        Assert.NotEmpty(config.Models);
        Assert.Equal(FixtureFirstModelName, config.Models[0].Name);
    }

    [Fact]
    public void CSharpUnlock_OnSwiftProducedFixture_RejectsWrongPassword()
    {
        var fixtureRoot = SwiftFixtureRoot();
        Assert.True(Directory.Exists(fixtureRoot), "Swift fixture missing");

        var unlocked = SsdEncryption.TryUnlockPortableConfig(
            fixtureRoot, "definitely-not-the-password", out var config, out var error);

        Assert.False(unlocked);
        Assert.Null(config);
        Assert.Equal("Incorrect password.", error);
    }

    [Fact]
    public void SwiftFixture_StateFile_UsesLowercaseCamelCaseFieldNames()
    {
        var statePath = Path.Combine(
            SwiftFixtureRoot(), SsdLayout.Config, SsdEncryption.StateFileName);
        Assert.True(File.Exists(statePath), $"Swift fixture state file missing: {statePath}");

        var node = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        // Field names — these are the keys Swift's JSONDecoder expects on the
        // Swift `EncryptionStateFile` Codable type. If C# ever stops emitting
        // camelCase, Swift will silently fail to parse and this test catches
        // the drift first.
        AssertOnlyExpectedKeys(node, new[]
        {
            "enabled", "scheme", "iterations", "encryptedConfigFile", "updatedAtUtc"
        });

        Assert.True((bool?)node["enabled"]);
        Assert.Equal(SsdEncryption.SchemeName, (string?)node["scheme"]);
        Assert.True((int?)node["iterations"] > 0);
        Assert.Equal(SsdEncryption.EncryptedConfigFileName, (string?)node["encryptedConfigFile"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)node["updatedAtUtc"]));
    }

    [Fact]
    public void SwiftFixture_EncryptedBlob_UsesLowercaseCamelCaseFieldNames()
    {
        var blobPath = Path.Combine(
            SwiftFixtureRoot(), SsdLayout.Config, SsdEncryption.EncryptedConfigFileName);
        Assert.True(File.Exists(blobPath), $"Swift fixture encrypted blob missing: {blobPath}");

        var node = JsonNode.Parse(File.ReadAllText(blobPath))!.AsObject();
        AssertOnlyExpectedKeys(node, new[]
        {
            "version", "scheme", "iterations", "salt", "nonce",
            "tag", "ciphertext", "createdAtUtc"
        });

        Assert.Equal(1, (int?)node["version"]);
        Assert.Equal(SsdEncryption.SchemeName, (string?)node["scheme"]);
        Assert.True((int?)node["iterations"] > 0);

        // Base64 length sanity: salt 16 bytes, nonce 12 bytes, tag 16 bytes.
        Assert.Equal(16, Convert.FromBase64String((string)node["salt"]!).Length);
        Assert.Equal(12, Convert.FromBase64String((string)node["nonce"]!).Length);
        Assert.Equal(16, Convert.FromBase64String((string)node["tag"]!).Length);
        Assert.NotEmpty(Convert.FromBase64String((string)node["ciphertext"]!));
    }

    [Fact]
    public async Task CSharpProducedBlob_HasFieldShapeSwiftCanParse()
    {
        // Reverse direction: build a blob via the C# production encrypt path,
        // then assert the resulting JSON uses the same lowercase camelCase
        // field names the Swift decoder requires. This guards against a
        // future C# JsonSerializerOptions change silently breaking Swift.
        var root = Path.Combine(Path.GetTempPath(), "free-ai-ssd-tests",
            "mac5-format-pin-" + Guid.NewGuid().ToString("N"));
        try
        {
            SsdLayout.EnsureStructure(root);
            var configPath = Path.Combine(root, SsdLayout.Config, "portable-config.json");
            await new PortableConfig
            {
                OllamaPort = 22222,
                Models = new List<ModelConfigEntry>
                {
                    new() { Name = "qwen2.5:3b", Status = ModelInstallStatus.Installed }
                }
            }.SaveAsync(configPath);
            await SsdEncryption.EnableConfigEncryptionAsync(root, configPath, "pw");

            var blobJson  = await File.ReadAllTextAsync(Path.Combine(root, SsdLayout.Config,
                SsdEncryption.EncryptedConfigFileName));
            var stateJson = await File.ReadAllTextAsync(Path.Combine(root, SsdLayout.Config,
                SsdEncryption.StateFileName));

            var blobNode  = JsonNode.Parse(blobJson)!.AsObject();
            var stateNode = JsonNode.Parse(stateJson)!.AsObject();

            AssertOnlyExpectedKeys(blobNode, new[]
            {
                "version", "scheme", "iterations", "salt", "nonce",
                "tag", "ciphertext", "createdAtUtc"
            });
            AssertOnlyExpectedKeys(stateNode, new[]
            {
                "enabled", "scheme", "iterations", "encryptedConfigFile", "updatedAtUtc"
            });
            Assert.Equal(SsdEncryption.SchemeName, (string?)blobNode["scheme"]);
            Assert.Equal(SsdEncryption.SchemeName, (string?)stateNode["scheme"]);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertOnlyExpectedKeys(JsonObject node, IEnumerable<string> expected)
    {
        var expectedSet = new HashSet<string>(expected, StringComparer.Ordinal);
        var actualSet   = new HashSet<string>(node.Select(kv => kv.Key), StringComparer.Ordinal);
        var missing = expectedSet.Except(actualSet).ToList();
        var extra   = actualSet.Except(expectedSet).ToList();
        Assert.True(missing.Count == 0,
            $"Expected JSON keys missing: {string.Join(", ", missing)}");
        Assert.True(extra.Count == 0,
            $"Unexpected extra JSON keys (would break Swift decoder): {string.Join(", ", extra)}");
    }

    /// <summary>
    /// Resolves <c>tests/Fixtures/MacEncryptedConfig/csharp-encrypted</c>
    /// relative to this source file, regardless of where the test binary
    /// is launched from.
    /// </summary>
    private static string SwiftFixtureRoot([CallerFilePath] string thisFile = "")
    {
        var testsDir = Path.GetDirectoryName(thisFile)!;
        return Path.Combine(testsDir, "Fixtures", "MacEncryptedConfig", "csharp-encrypted");
    }
}
