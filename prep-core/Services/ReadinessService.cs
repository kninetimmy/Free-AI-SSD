using System.Text.Json;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Models;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.PrepApp.Services;

public sealed class ReadinessService : IReadinessService
{
    private readonly IModelService _modelService;

    public ReadinessService(IModelService modelService)
    {
        _modelService = modelService;
    }

    public async Task<List<ReadinessItem>> RunReadinessChecksAsync(string root, Action<string> onLog, CancellationToken ct)
    {
        var checks = new List<ReadinessItem>();

        var runnerDir = Path.Combine(root, SsdLayout.Runner);
        var runnerExe = Path.Combine(runnerDir, "FreeAiSsd.Runner.exe");
        checks.Add(File.Exists(runnerExe)
            ? ReadinessItem.Pass("Windows runner files present")
            : ReadinessItem.Warn("Windows runner files present", "Runner executable not found in SSD/windows/runner (ok if mac-only prep)."));

        var macRunnerDir = Path.Combine(root, SsdLayout.MacRunner);
        checks.Add(Directory.Exists(macRunnerDir)
            ? ReadinessItem.Pass("macOS Runner.app present")
            : ReadinessItem.Warn("macOS Runner.app present", "Runner.app not found at SSD root (ok if Windows-only prep)."));

        // MAC29: accept encrypted-or-plaintext config. Mac PrepApp ships only the
        // encrypted blob (MAC5 invariant); pre-MAC30 Windows PrepApp ships plaintext.
        // The encrypted blob can be validated as well-formed JSON with the expected
        // scheme without needing the passphrase.
        var configPath = Path.Combine(root, new PortableConfig().ConfigRelativePath);
        var encryptedConfigPath = Path.Combine(root, SsdLayout.Config, SsdEncryption.EncryptedConfigFileName);
        var (config, configIsValid, plaintextConfigLoaded) =
            await LoadConfigForReadinessAsync(configPath, encryptedConfigPath);

        checks.Add(configIsValid
            ? ReadinessItem.Pass("Config.json valid")
            : ReadinessItem.Fail("Config.json valid", "Config missing or unreadable; defaults loaded."));

        var ollamaExe = Path.Combine(root, config.OllamaRelativePath);
        checks.Add(File.Exists(ollamaExe)
            ? ReadinessItem.Pass("Windows Ollama executable present")
            : ReadinessItem.Warn("Windows Ollama executable present", "ollama.exe missing under SSD/windows/tools/ollama (ok if mac-only prep)."));

        // MAC26: the macOS Ollama distribution is a GUI app bundle. The
        // self-contained CLI server lives at
        // Ollama.app/Contents/Resources/ollama, not at the bundle root.
        var macOllamaExe = Path.Combine(root, SsdLayout.MacOllama, "Ollama.app", "Contents", "Resources", "ollama");
        checks.Add(File.Exists(macOllamaExe)
            ? ReadinessItem.Pass("macOS Ollama executable present")
            : ReadinessItem.Warn("macOS Ollama executable present", "Ollama.app/Contents/Resources/ollama missing under SSD/mac/tools/ollama (ok if Windows-only prep)."));

        var modelsDir = Path.Combine(root, SsdLayout.Models);
        checks.Add(Directory.Exists(modelsDir)
            ? ReadinessItem.Pass("Models directory present")
            : ReadinessItem.Fail("Models directory present", "SSD/models directory is missing."));

        var prereqDir = Path.Combine(root, SsdLayout.Prereqs);
        var prereqManifestPath = PrereqCatalog.GetManifestPath(root);
        var prereqManifest = PrereqManifest.Load(prereqManifestPath);
        var prereqIssues = PrereqInstallValidator.ValidateBundleHealth(prereqDir, prereqManifest);
        checks.Add(prereqIssues.Count == 0
            ? ReadinessItem.Pass("Prereq bundle verified")
            : ReadinessItem.Warn("Prereq bundle verified", "Warning: " + string.Join("; ", prereqIssues.Take(3))));

        // MAC29: the model check switches to disk-truth. Mac sidecar's pull-model
        // arm doesn't write back to the encrypted config (the passphrase is zeroized
        // before pulls run), so config.Models is empty on a Mac-prepped SSD even
        // after a successful pull. Discover models from manifests on disk; verify
        // each blob against its config-pinned SHA when one exists, otherwise against
        // the blob's own filename digest. Ollama blobs are content-addressed by
        // filename (`sha256-<hex>`), so the self-consistency check still catches
        // tampering even without a pinned hash.
        var discovered = ModelOperations.DiscoverModelsOnDisk(modelsDir);
        if (discovered.Count == 0)
        {
            checks.Add(ReadinessItem.Fail("≥1 installed model", "No models found on disk."));
            return checks;
        }

        var pinnedHashes = config.Models
            .Where(m => !string.IsNullOrWhiteSpace(m.Sha256))
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Sha256!, StringComparer.OrdinalIgnoreCase);

        var verifiedAny = false;
        var invalidModels = new List<string>();
        foreach (var modelName in discovered)
        {
            var blob = ModelOperations.FindModelBlobForModel(modelsDir, modelName);
            if (blob is null)
            {
                invalidModels.Add($"{modelName} (blob missing)");
                MarkFailed(config, modelName, plaintextConfigLoaded);
                continue;
            }

            string? expectedHash = pinnedHashes.TryGetValue(modelName, out var pinned)
                ? pinned
                : TryParseFilenameSha256(blob);

            if (string.IsNullOrEmpty(expectedHash))
            {
                invalidModels.Add($"{modelName} (blob filename does not encode sha256)");
                continue;
            }

            var ok = await _modelService.VerifyModelAsync(modelsDir, modelName, expectedHash, _ => { }, ct);
            if (ok)
            {
                verifiedAny = true;
                if (plaintextConfigLoaded)
                {
                    var entry = config.Models.FirstOrDefault(m =>
                        string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase));
                    if (entry is not null)
                    {
                        entry.LastVerifiedUtc = DateTime.UtcNow;
                    }
                }
            }
            else
            {
                invalidModels.Add($"{modelName} (hash mismatch)");
                MarkFailed(config, modelName, plaintextConfigLoaded);
            }
        }

        if (plaintextConfigLoaded)
        {
            await config.SaveAsync(configPath);
        }

        if (verifiedAny)
        {
            if (invalidModels.Count > 0)
            {
                onLog("Some models failed integrity check: " + string.Join(", ", invalidModels));
            }
            checks.Add(ReadinessItem.Pass("≥1 installed model"));
        }
        else
        {
            onLog("Model integrity check failed for: " + string.Join(", ", invalidModels));
            checks.Add(ReadinessItem.Fail("≥1 installed model", "Integrity failed: " + string.Join("; ", invalidModels)));
        }

        return checks;
    }

    private static async Task<(PortableConfig Config, bool ConfigValid, bool PlaintextLoaded)> LoadConfigForReadinessAsync(
        string plaintextPath,
        string encryptedPath)
    {
        if (File.Exists(plaintextPath))
        {
            var (loaded, isValid) = await PortableConfig.LoadWithValidationAsync(plaintextPath);
            if (isValid)
            {
                return (loaded, true, true);
            }
        }

        if (File.Exists(encryptedPath))
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(encryptedPath);
                using var doc = JsonDocument.Parse(bytes);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("scheme", out var schemeProp) &&
                    schemeProp.ValueKind == JsonValueKind.String &&
                    string.Equals(schemeProp.GetString(), SsdEncryption.SchemeName, StringComparison.Ordinal))
                {
                    return (new PortableConfig(), true, false);
                }
            }
            catch
            {
                // Fall through to invalid.
            }
        }

        return (new PortableConfig(), false, false);
    }

    private static void MarkFailed(PortableConfig config, string modelName, bool plaintextConfigLoaded)
    {
        if (!plaintextConfigLoaded) return;
        var entry = config.Models.FirstOrDefault(m =>
            string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase));
        if (entry is not null)
        {
            entry.Status = ModelInstallStatus.Failed;
        }
    }

    private static string? TryParseFilenameSha256(string blobPath)
    {
        var name = Path.GetFileName(blobPath);
        const string prefix = "sha256-";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hex = name[prefix.Length..];
        if (hex.Length == 0)
        {
            return null;
        }

        foreach (var c in hex)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return null;
            }
        }

        return hex;
    }
}
