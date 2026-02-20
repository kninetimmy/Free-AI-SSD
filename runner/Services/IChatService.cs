using FreeAiSsd.Shared;

namespace FreeAiSsd.Runner.Services;

/// <summary>
/// Result of a chat prompt, including the LLM response text and any RAG source citations.
/// </summary>
public record ChatResponse(string ResponseText, List<string>? Sources, bool UsedRagContext);

public interface IChatService
{
    event Action<string>? LogMessage;

    /// <summary>
    /// Sends a prompt to the running Ollama instance. If a document library is active,
    /// performs RAG retrieval to augment the prompt with relevant context.
    /// </summary>
    Task<ChatResponse> SendPromptAsync(string model, string userPrompt, string host, PortableConfig config);
}
