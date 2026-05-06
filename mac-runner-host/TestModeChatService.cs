using FreeAiSsd.Runner.Services;
using FreeAiSsd.Shared;

namespace FreeAiSsd.MacRunnerHost;

/// <summary>
/// Test-only <see cref="IChatService"/> installed when the host is launched
/// with <c>--test-mode</c>. Returns canned responses without touching Ollama
/// so CI can spawn the published binary and exercise <c>/api/chat</c> +
/// <c>/api/chat/stream</c> end-to-end. Production launches never set this
/// flag; the Swift mac-runner spawns the host without it.
/// </summary>
internal sealed class TestModeChatService : IChatService
{
    public event Action<string>? LogMessage;

    public Task<ChatResult> SendPromptAsync(string model, string userPrompt, string host, PortableConfig config)
    {
        LogMessage?.Invoke($"test-mode chat: model={model}, host={host}, promptLength={userPrompt.Length}");
        var response = new ChatResponse(
            ResponseText: $"test-mode response for prompt: {userPrompt}",
            Sources: null,
            UsedRagContext: false);
        return Task.FromResult<ChatResult>(new ChatResult.Success(response));
    }

    public async Task<ChatResult> SendPromptStreamingAsync(
        string model,
        string userPrompt,
        string host,
        PortableConfig config,
        Func<string, Task> onToken,
        CancellationToken cancellationToken = default)
    {
        var tokens = new[] { "test", "-", "mode", " ", "stream" };
        var assembled = new System.Text.StringBuilder();
        foreach (var token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await onToken(token);
            assembled.Append(token);
        }

        return new ChatResult.Success(new ChatResponse(assembled.ToString(), null, false));
    }
}
