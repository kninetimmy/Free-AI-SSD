using FreeAiSsd.Shared;

namespace FreeAiSsd.Runner.Services;

/// <summary>
/// Result of a chat prompt, including the LLM response text and any RAG source citations.
/// </summary>
public record ChatResponse(string ResponseText, List<string>? Sources, bool UsedRagContext);

/// <summary>
/// Metadata returned at the start of a streaming response, before tokens arrive.
/// Contains RAG sources so the UI can display citations immediately.
/// </summary>
public record StreamingChatContext(List<string>? Sources, bool UsedRagContext);

public abstract record ChatResult
{
    public sealed record Success(ChatResponse Response) : ChatResult;
    public sealed record RagRetrievalFailed(ChatResponse Response, string RagError) : ChatResult;
    public sealed record Failure(string ErrorMessage) : ChatResult;
}

public interface IChatService
{
    event Action<string>? LogMessage;

    /// <summary>
    /// Sends a prompt to the running Ollama instance. If a document library is active,
    /// performs RAG retrieval to augment the prompt with relevant context.
    /// </summary>
    Task<ChatResult> SendPromptAsync(string model, string userPrompt, string host, PortableConfig config);

    /// <summary>
    /// Sends a prompt and streams the response token-by-token.
    /// <paramref name="onToken"/> is called with each incremental text fragment and awaited.
    /// Returns the final assembled response and RAG metadata.
    /// </summary>
    Task<ChatResult> SendPromptStreamingAsync(
        string model, string userPrompt, string host, PortableConfig config,
        Func<string, Task> onToken, CancellationToken cancellationToken = default);
}
