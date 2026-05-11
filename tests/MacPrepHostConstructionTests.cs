using FreeAiSsd.MacPrepHost;
using FreeAiSsd.MacPrepHost.Services;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Models;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.Tests;

/// <summary>
/// MAC17 guardrail mirroring <see cref="PrepCoreConstructionTests"/> for the
/// MAC17 sidecar. Proves the mac-prep-host's HostLifetime constructs on a
/// plain net8.0 host without reaching for WPF / WindowsDesktop, exercising
/// every prep-core service the sidecar wires. Plus a NoOpDialogService
/// behavioral pin so the defense-in-depth posture (refuse all confirmations,
/// return null/Cancel for prompts) doesn't silently regress.
/// </summary>
public sealed class MacPrepHostConstructionTests : IDisposable
{
    private readonly string _tempRoot;

    public MacPrepHostConstructionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "freeai-mac17-prephost-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);
        SsdLayout.EnsureStructure(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public void ExtractHuggingFaceBareRepoId_StripsPrefixAndQuantSuffix()
    {
        // 2026-05-12 field test: HF's /api/models/{repoId} 404s on a
        // tag with a `:quant` suffix (e.g. unsloth/Qwen3.5-9B-GGUF:IQ2_M).
        // PullModelAsync's HF size precheck now strips both the
        // `hf.co/` prefix and the trailing `:quant` before calling
        // FetchSiblingsAsync. Pin: parent and child tags both map to
        // the same bare `owner/repo`.
        Assert.Equal("unsloth/Qwen3.5-9B-GGUF",
            HostLifetime.ExtractHuggingFaceBareRepoId("hf.co/unsloth/Qwen3.5-9B-GGUF:IQ2_M"));
        Assert.Equal("unsloth/Qwen3.5-9B-GGUF",
            HostLifetime.ExtractHuggingFaceBareRepoId("hf.co/unsloth/Qwen3.5-9B-GGUF"));
        // Case-insensitive prefix match (Ollama accepts HF.CO too).
        Assert.Equal("owner/repo",
            HostLifetime.ExtractHuggingFaceBareRepoId("HF.CO/owner/repo:Q4_K_M"));
        // Empty / missing prefix passes through (caller still
        // validates via FetchSiblingsAsync's owner/repo regex).
        Assert.Equal(string.Empty, HostLifetime.ExtractHuggingFaceBareRepoId(string.Empty));
        Assert.Equal("owner/repo",
            HostLifetime.ExtractHuggingFaceBareRepoId("owner/repo:Q5_K_M"));
    }

    [Fact]
    public void ExtractPullModelTag_RecoversTagAfterCommand()
    {
        // 2026-05-12 regression: when PullModelAsync throws BEFORE the
        // inner try (e.g. ollamaExe missing, staging-precheck disk-full),
        // Program.cs's fallback emits a `pull-model ok=false` result so
        // Swift's PrepHostController unblocks. The result line needs the
        // model tag — extract it from the command line.
        Assert.Equal("hf.co/owner/repo:Q4_K_M",
            HostRunner.ExtractPullModelTag("pull-model hf.co/owner/repo:Q4_K_M"));
        Assert.Equal("llama3:8b", HostRunner.ExtractPullModelTag("pull-model llama3:8b"));
        // Tolerates extra whitespace.
        Assert.Equal("hf.co/owner/repo",
            HostRunner.ExtractPullModelTag("  pull-model    hf.co/owner/repo  "));
        // Empty payload / unrelated command → empty (still safe: the
        // fallback result just omits modelTag, Swift still unblocks).
        Assert.Equal(string.Empty, HostRunner.ExtractPullModelTag("pull-model"));
        Assert.Equal(string.Empty, HostRunner.ExtractPullModelTag(""));
        Assert.Equal(string.Empty, HostRunner.ExtractPullModelTag("ensure-structure"));
    }

    [Fact]
    public void HostLifetime_ConstructsOnPlainNet8_WithoutWpfHost()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        // Construction itself exercises every prep-core service ctor the
        // sidecar wires, plus NoOpDialogService and SsdLogger over a temp
        // SSD root. A successful construct on the net8.0 test host is the
        // strongest portable proof we can make from Windows CI that the
        // Mac sidecar's DI graph stays plain.
        var lifetime = new HostLifetime(_tempRoot, "http://127.0.0.1:11434", stdout, stderr, testMode: true);
        Assert.NotNull(lifetime);

        // Start emits a 'ready' line synchronously — the prep sidecar has
        // no async startup work the way mac-runner-host's StartAsync does.
        lifetime.Start();
        Assert.Contains("ready", stdout.ToString());
    }

    [Fact]
    public async Task HostLifetime_TestModeReadiness_EmitsSyntheticResult()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var lifetime = new HostLifetime(_tempRoot, "http://127.0.0.1:11434", stdout, stderr, testMode: true);
        lifetime.Start();

        // Test mode short-circuits the actual readiness check (which
        // requires an Ollama presence, a populated SSD, etc.) and emits
        // a synthetic OK result. This is the in-process complement to the
        // CI binary smoke that publishes mac-prep-host and pipes commands.
        await lifetime.HandleCommandAsync("readiness");

        var output = stdout.ToString();
        Assert.Contains("result: readiness", output);
        Assert.Contains("\"ok\":true", output);
        Assert.Contains("\"testMode\":true", output);
    }

    [Fact]
    public async Task HostLifetime_TestModeStageCommands_AllReturnOk()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var lifetime = new HostLifetime(_tempRoot, "http://127.0.0.1:11434", stdout, stderr, testMode: true);
        lifetime.Start();

        await lifetime.HandleCommandAsync("stage-runner");
        await lifetime.HandleCommandAsync("stage-ollama");
        await lifetime.HandleCommandAsync("stage-prereqs");

        var output = stdout.ToString();
        Assert.Contains("result: stage-runner", output);
        Assert.Contains("result: stage-ollama", output);
        Assert.Contains("result: stage-prereqs", output);
    }

    [Fact]
    public async Task HostLifetime_DiscoverModels_ReturnsEmptyOnFreshRoot()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var lifetime = new HostLifetime(_tempRoot, "http://127.0.0.1:11434", stdout, stderr, testMode: false);
        lifetime.Start();

        // discover-models is read-only — no test-mode bypass needed. On a
        // freshly-laid-down SSD root with no models pulled yet, the result
        // should be an empty array.
        await lifetime.HandleCommandAsync("discover-models");

        var output = stdout.ToString();
        Assert.Contains("result: discover-models", output);
        Assert.Contains("\"models\":[]", output);
    }

    [Fact]
    public async Task HostLifetime_PullModelWithoutTag_ThrowsClearError()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var lifetime = new HostLifetime(_tempRoot, "http://127.0.0.1:11434", stdout, stderr, testMode: true);
        lifetime.Start();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifetime.HandleCommandAsync("pull-model"));
        Assert.Contains("model tag", ex.Message);
    }

    [Fact]
    public async Task HostLifetime_VerifyModelWithBadArgs_ThrowsClearError()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var lifetime = new HostLifetime(_tempRoot, "http://127.0.0.1:11434", stdout, stderr, testMode: true);
        lifetime.Start();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifetime.HandleCommandAsync("verify-model only-one-arg"));
        Assert.Contains("expected-hash", ex.Message);
    }

    [Fact]
    public void NoOpDialogService_RefusesAllConfirmations()
    {
        // Defense-in-depth pin: the sidecar's IDialogService should never
        // silently approve a mutation. If a future prep-core code path
        // grows a confirmation prompt and reaches the Mac sidecar
        // accidentally, refusing is safer than approving. Mac UX
        // confirmations live in Swift; the sidecar dialog never runs.
        IDialogService dialog = new NoOpDialogService();

        Assert.False(dialog.Confirm("anything", "title"));
        Assert.False(dialog.ConfirmFixedDrive("/Volumes/SystemDrive"));
        Assert.False(dialog.ConfirmSizingWarnings(new[] { "warning" }));
        Assert.False(dialog.ConfirmErase("/Volumes/Test", "100 GB", "ExFAT"));
        Assert.False(dialog.ConfirmPrereqRefresh());
        Assert.Null(dialog.PromptForEncryptionPassword());
        Assert.Equal(ModelRemoveChoice.Cancel, dialog.PromptRemoveModel("any-model"));
    }
}
