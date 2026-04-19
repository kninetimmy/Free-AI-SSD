using System.Reflection;
using FreeAiSsd.Runner.Services;
using FreeAiSsd.Shared;
using Xunit;

namespace FreeAiSsd.Tests;

/// <summary>
/// Regression tests for X8: <see cref="WhisperSpeechToTextService.InitializeAsync"/>
/// used to call the public <c>Dispose()</c> to reset state between model loads, which
/// disposed the <c>_transcriptionGate</c> semaphore along with the model. Any subsequent
/// <c>TranscribeAudioAsync</c> call then threw <see cref="ObjectDisposedException"/>
/// on <c>SemaphoreSlim.WaitAsync</c>. Fix splits model teardown (<c>ReleaseModel</c>)
/// from full disposal so the gate survives re-init.
/// </summary>
public class WhisperSpeechToTextServiceTests
{
    [Fact]
    public void ReleaseModel_DoesNotDisposeTranscriptionGate()
    {
        var svc = new WhisperSpeechToTextService();
        try
        {
            InvokeReleaseModel(svc);

            var gate = GetTranscriptionGate(svc);
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

            var gate = GetTranscriptionGate(svc);
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

            var gate = GetTranscriptionGate(svc);
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
    public void Dispose_DisposesTranscriptionGate()
    {
        var svc = new WhisperSpeechToTextService();
        var gate = GetTranscriptionGate(svc);
        svc.Dispose();

        Assert.Throws<ObjectDisposedException>(() => gate.Wait(0));
    }

    private static void InvokeReleaseModel(WhisperSpeechToTextService svc)
    {
        var method = typeof(WhisperSpeechToTextService)
            .GetMethod("ReleaseModel", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(svc, null);
    }

    private static SemaphoreSlim GetTranscriptionGate(WhisperSpeechToTextService svc)
    {
        var field = typeof(WhisperSpeechToTextService)
            .GetField("_transcriptionGate", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (SemaphoreSlim)field!.GetValue(svc)!;
    }
}
