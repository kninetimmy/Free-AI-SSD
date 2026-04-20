using System.Reflection;
using FreeAiSsd.Runner.Services;
using FreeAiSsd.Shared;
using Xunit;

namespace FreeAiSsd.Tests;

/// <summary>
/// Regression tests for the <see cref="WhisperSpeechToTextService"/> lifecycle.
///
/// X8 (commit 591a39b) fixed the original bug where <c>InitializeAsync</c> called
/// <c>Dispose()</c> to reset state between model loads, which disposed the shared
/// semaphore along with the model and broke every subsequent transcription with
/// <see cref="ObjectDisposedException"/>.
///
/// This follow-up (Codex adversarial review) adds service-level lifecycle
/// serialization so the singleton cannot be corrupted by concurrent init/dispose
/// from the three shared consumers (MainWindow, PttVoicePipelineService,
/// RunnerLocalApiService). The transcription gate was folded into a single
/// <c>_lifecycleGate</c> that covers Initialize, Transcribe, and Dispose.
/// </summary>
public class WhisperSpeechToTextServiceTests
{
    [Fact]
    public void ReleaseModel_DoesNotDisposeLifecycleGate()
    {
        var svc = new WhisperSpeechToTextService();
        try
        {
            InvokeReleaseModel(svc);

            var gate = GetLifecycleGate(svc);
            // If ReleaseModel disposed the gate, Wait(0) throws ObjectDisposedException.
            Assert.True(gate.Wait(0));
            gate.Release();
        }
        finally
        {
            svc.Dispose();
        }
    }

    [Fact]
    public void ReleaseModel_CalledRepeatedly_IsSafe()
    {
        var svc = new WhisperSpeechToTextService();
        try
        {
            InvokeReleaseModel(svc);
            InvokeReleaseModel(svc);
            InvokeReleaseModel(svc);

            var gate = GetLifecycleGate(svc);
            Assert.True(gate.Wait(0));
            gate.Release();
        }
        finally
        {
            svc.Dispose();
        }
    }

    [Fact]
    public async Task InitializeAsync_FailsOnMissingModel_LeavesGateUsable()
    {
        // Point at a directory with no model and no internet fallback available in CI.
        // We only care that a failed init doesn't dispose the gate; any throw is fine.
        var svc = new WhisperSpeechToTextService();
        try
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "freeai-whisper-test-" + Guid.NewGuid());
            Directory.CreateDirectory(tempRoot);
            var config = new PortableConfig { WhisperModelSize = WhisperModelSize.Tiny };

            try
            {
                await svc.InitializeAsync(tempRoot, config);
            }
            catch
            {
                // Download or load failure is expected in the test environment.
            }

            var gate = GetLifecycleGate(svc);
            Assert.True(gate.Wait(0));
            gate.Release();

            Directory.Delete(tempRoot, recursive: true);
        }
        finally
        {
            svc.Dispose();
        }
    }

    [Fact]
    public void Dispose_DisposesLifecycleGate()
    {
        var svc = new WhisperSpeechToTextService();
        var gate = GetLifecycleGate(svc);
        svc.Dispose();

        Assert.Throws<ObjectDisposedException>(() => gate.Wait(0));
    }

    [Fact]
    public async Task TranscribeAudioAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var svc = new WhisperSpeechToTextService();
        svc.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => svc.TranscribeAudioAsync(new byte[16], CancellationToken.None));
    }

    [Fact]
    public async Task InitializeAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var svc = new WhisperSpeechToTextService();
        svc.Dispose();

        var config = new PortableConfig { WhisperModelSize = WhisperModelSize.Tiny };
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => svc.InitializeAsync(Path.GetTempPath(), config));
    }

    [Fact]
    public async Task InitializeAsync_ConcurrentCallers_SerializeThroughLifecycleGate()
    {
        // Simulates the singleton race Codex flagged: MainWindow, PttVoicePipelineService,
        // and RunnerLocalApiService can all call InitializeAsync on the same instance at
        // first-use. With the lifecycle gate the service must not crash or tear down a
        // concurrently-loaded model. We use a missing-model path so every init throws —
        // the assertion is that the gate survives and remains acquirable.
        var svc = new WhisperSpeechToTextService();
        try
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "freeai-whisper-test-" + Guid.NewGuid());
            Directory.CreateDirectory(tempRoot);
            var config = new PortableConfig { WhisperModelSize = WhisperModelSize.Tiny };

            async Task TryInit()
            {
                try { await svc.InitializeAsync(tempRoot, config); }
                catch { /* download/load failure is expected */ }
            }

            var tasks = new[] { TryInit(), TryInit(), TryInit() };
            await Task.WhenAll(tasks);

            var gate = GetLifecycleGate(svc);
            Assert.True(gate.Wait(0));
            gate.Release();

            Directory.Delete(tempRoot, recursive: true);
        }
        finally
        {
            svc.Dispose();
        }
    }

    [Fact]
    public async Task TranscribeStreamAsync_WhenProcessorNull_ReturnsTranscriptionResultFailure()
    {
        // Service not initialized — _processor is null.
        // The InvalidOperationException thrown inside the try block is caught
        // by the general handler and returned as TranscriptionResult.Failure.
        var svc = new WhisperSpeechToTextService();
        try
        {
            using var stream = new MemoryStream(new byte[64]);
            var result = await svc.TranscribeStreamAsync(stream, CancellationToken.None);

            var failure = Assert.IsType<TranscriptionResult.Failure>(result);
            Assert.Contains("not loaded", failure.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            svc.Dispose();
        }
    }

    [Fact(Skip = "Requires Whisper runtime — verifies contract: silence returns Success(\"\"), not Failure")]
    public async Task TranscribeAudioAsync_WhenNoSpeech_ReturnsSuccessWithEmptyText()
    {
        // If Whisper processes audio but detects no speech, segments is empty.
        // Result must be TranscriptionResult.Success(""), not Failure.
        // Empty Success is the "no speech" case; Failure means the processor crashed.
        _ = Task.CompletedTask; // placeholder
        await Task.CompletedTask;
    }

    private static void InvokeReleaseModel(WhisperSpeechToTextService svc)
    {
        var method = typeof(WhisperSpeechToTextService)
            .GetMethod("ReleaseModel", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(svc, null);
    }

    private static SemaphoreSlim GetLifecycleGate(WhisperSpeechToTextService svc)
    {
        var field = typeof(WhisperSpeechToTextService)
            .GetField("_lifecycleGate", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (SemaphoreSlim)field!.GetValue(svc)!;
    }
}
