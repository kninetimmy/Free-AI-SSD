using FreeAiSsd.PrepApp;
using FreeAiSsd.PrepApp.Services;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Models;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.Tests;

/// <summary>
/// MAC16 guardrail: every prep-core service must construct on a plain net8.0
/// host without reaching for WPF, WindowsDesktop, or any other Windows-only
/// surface. The test project itself is plain net8.0 (no UseWPF), so a
/// successful construction here is the strongest portable proof we can run
/// from the test host. Mirrors RunnerCoreConstructionTests for the
/// runner-core extraction (MAC3).
/// </summary>
public sealed class PrepCoreConstructionTests : IDisposable
{
    private readonly string _tempRoot;

    public PrepCoreConstructionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "freeai-prepcore-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);
        SsdLayout.EnsureStructure(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public void CoreServices_ConstructWithoutWpfHost()
    {
        var dialogStub = new DialogStub();

        IArtifactStagingService staging = new ArtifactStagingService();
        IPrereqService prereqs = new PrereqService(dialogStub);
        IOllamaPackageService ollama = new OllamaPackageService();
        IModelService models = new ModelService();
        IReadinessService readiness = new ReadinessService(models);
        IEncryptionService encryption = new EncryptionService();

        Assert.NotNull(staging);
        Assert.NotNull(prereqs);
        Assert.NotNull(ollama);
        Assert.NotNull(models);
        Assert.NotNull(readiness);
        Assert.NotNull(encryption);

        // Sanity: a method that touches no Win-only surface still works.
        var emptyModelsRoot = Path.Combine(_tempRoot, "models-empty");
        Directory.CreateDirectory(emptyModelsRoot);
        Assert.Empty(models.DiscoverModelsOnDisk(emptyModelsRoot));
    }

    [Fact]
    public void StarterModelCatalogLoader_ResolvesEmbeddedFallback_WithoutWpfHost()
    {
        // Point at a directory where the on-disk file does not exist so the
        // loader has to fall through to the embedded resource shipped by
        // prep-core. Verifies <RootNamespace>FreeAiSsd.PrepApp</RootNamespace>
        // keeps the embedded resource name stable across the move.
        var emptyDir = Path.Combine(_tempRoot, "no-catalog-here");
        Directory.CreateDirectory(emptyDir);

        var result = StarterModelCatalogLoader.Load(emptyDir);

        Assert.NotNull(result);
        Assert.NotNull(result.Catalog);
        // The shipped catalog must contain at least one valid entry; an empty
        // fallback would mean the embedded resource lookup silently failed.
        Assert.NotEmpty(result.Catalog.Models);
    }

    [Fact]
    public void StarterModelCatalogLoader_BackfillsParametersBillionFromParamsToken()
    {
        // 2026-05-12 regression: bundled starter-models.json predates the
        // ParametersBillion field, so the C3 ≤7B cap let `phi3:14b` /
        // `qwen2.5:14b` / `codellama:13b` survive the filter (nil passes
        // through). Loader now derives the numeric value from the human-
        // readable `params` token at deserialize time. Pin: every shipped
        // entry whose params is a recognizable size token must arrive with
        // ParametersBillion populated.
        var emptyDir = Path.Combine(_tempRoot, "backfill-check");
        Directory.CreateDirectory(emptyDir);

        var result = StarterModelCatalogLoader.Load(emptyDir);

        Assert.NotNull(result.Catalog);
        var fourteenB = result.Catalog.Models.FirstOrDefault(m => m.Tag == "phi3:14b");
        Assert.NotNull(fourteenB);
        Assert.Equal(14.0, fourteenB!.ParametersBillion);

        var sevenB = result.Catalog.Models.FirstOrDefault(m => m.Tag == "mistral:7b");
        Assert.NotNull(sevenB);
        Assert.Equal(7.0, sevenB!.ParametersBillion);

        var twoB = result.Catalog.Models.FirstOrDefault(m => m.Tag == "gemma2:2b");
        Assert.NotNull(twoB);
        Assert.Equal(2.0, twoB!.ParametersBillion);
    }

    private sealed class DialogStub : IDialogService
    {
        public void ShowInfo(string message, string title) { }
        public void ShowWarning(string message, string title) { }
        public void ShowError(string message, string title) { }
        public bool Confirm(string message, string title) => false;
        public bool ConfirmFixedDrive(string driveRoot) => false;
        public bool ConfirmSizingWarnings(IReadOnlyList<string> warnings) => false;
        public bool ConfirmErase(string driveRoot, string sizeDisplay, string fileSystem) => false;
        public string? PromptForEncryptionPassword() => null;
        public ModelRemoveChoice PromptRemoveModel(string modelName) => ModelRemoveChoice.Cancel;
        public bool ConfirmPrereqRefresh() => false;
    }
}
