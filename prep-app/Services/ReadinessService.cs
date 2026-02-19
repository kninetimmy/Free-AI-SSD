using System.IO;
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
            : ReadinessItem.Warn("macOS Runner.app present", "Runner.app not found in SSD/mac (ok if Windows-only prep)."));

        var configPath = Path.Combine(root, new PortableConfig().ConfigRelativePath);
        var (config, configIsValid) = await PortableConfig.LoadWithValidationAsync(configPath);

        checks.Add(configIsValid
            ? ReadinessItem.Pass("Config.json valid")
            : ReadinessItem.Fail("Config.json valid", "Config missing or unreadable; defaults loaded."));

        var ollamaExe = Path.Combine(root, config.OllamaRelativePath);
        checks.Add(File.Exists(ollamaExe)
            ? ReadinessItem.Pass("Windows Ollama executable present")
            : ReadinessItem.Warn("Windows Ollama executable present", "ollama.exe missing under SSD/windows/tools/ollama (ok if mac-only prep)."));

        var macOllamaExe = Path.Combine(root, SsdLayout.MacOllama, "ollama");
        checks.Add(File.Exists(macOllamaExe)
            ? ReadinessItem.Pass("macOS Ollama executable present")
            : ReadinessItem.Warn("macOS Ollama executable present", "ollama missing under SSD/mac/tools/ollama (ok if Windows-only prep)."));

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

        var installedModels = config.Models.Where(m => m.Status == ModelInstallStatus.Installed).ToList();
        if (installedModels.Count == 0)
        {
            checks.Add(ReadinessItem.Fail("≥1 installed model", "No models marked Installed in config."));
        }
        else
        {
            var invalidModels = new List<string>();
            foreach (var model in installedModels)
            {
                if (string.IsNullOrWhiteSpace(model.Sha256))
                {
                    invalidModels.Add($"{model.Name} (missing stored hash)");
                    model.Status = ModelInstallStatus.Failed;
                    continue;
                }

                var ok = await _modelService.VerifyModelAsync(modelsDir, model.Name, model.Sha256, _ => { }, ct);
                if (!ok)
                {
                    invalidModels.Add($"{model.Name} (hash mismatch or blob missing)");
                    model.Status = ModelInstallStatus.Failed;
                }
                else
                {
                    model.LastVerifiedUtc = DateTime.UtcNow;
                }
            }

            if (invalidModels.Count > 0)
            {
                await config.SaveAsync(configPath);
                onLog("Model integrity check failed for: " + string.Join(", ", invalidModels));
                checks.Add(ReadinessItem.Fail("≥1 installed model", "Integrity failed: " + string.Join("; ", invalidModels)));
            }
            else
            {
                await config.SaveAsync(configPath);
                checks.Add(ReadinessItem.Pass("≥1 installed model"));
            }
        }

        return checks;
    }
}
