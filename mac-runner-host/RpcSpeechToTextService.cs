using FreeAiSsd.Runner.Services;
using FreeAiSsd.Shared;

namespace FreeAiSsd.MacRunnerHost;

// Task #106. The Mac sidecar's ISpeechToTextService: instead of a local Whisper
// model, it round-trips the PCM the endpoint hands it to the Swift parent's
// on-device SFSpeechRecognizer (decision #168) over VoiceRpcChannel.
//
// IsModelLoaded is reported true so RunnerLocalApiService.EnsureSttInitializedAsync
// short-circuits without calling InitializeAsync — real availability lives in the
// Swift recognizer and a failure there comes back as a Failure result, which the
// endpoint maps to a 500 just like a Whisper failure on Windows.
internal sealed class RpcSpeechToTextService : ISpeechToTextService
{
    private readonly VoiceRpcChannel _channel;

    public event Action<string>? LogMessage;

    public RpcSpeechToTextService(VoiceRpcChannel channel)
    {
        _channel = channel;
    }

    public bool IsModelLoaded => true;

    public Task InitializeAsync(string ssdRoot, PortableConfig config) => Task.CompletedTask;

    public Task<TranscriptionResult> TranscribeAudioAsync(byte[] audioData)
        => TranscribeAudioAsync(audioData, CancellationToken.None);

    public async Task<TranscriptionResult> TranscribeAudioAsync(byte[] audioData, CancellationToken cancellationToken)
    {
        try
        {
            var wav = VoiceRpcProtocol.WrapPcm16ToWav(audioData);
            var response = await _channel.SendSttRequestAsync(wav, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(response.Error))
            {
                return new TranscriptionResult.Failure(response.Error);
            }
            return new TranscriptionResult.Success(response.Text ?? string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"STT RPC failed: {ex.Message}");
            return new TranscriptionResult.Failure(ex.Message);
        }
    }

    public Task<TranscriptionResult> TranscribeStreamAsync(Stream audioStream)
        => TranscribeStreamAsync(audioStream, CancellationToken.None);

    public async Task<TranscriptionResult> TranscribeStreamAsync(Stream audioStream, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        await audioStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return await TranscribeAudioAsync(ms.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
    }
}
