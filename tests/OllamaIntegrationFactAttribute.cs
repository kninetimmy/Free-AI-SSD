using Xunit;

namespace FreeAiSsd.Tests;

/// <summary>
/// xUnit [Fact] that auto-skips when FREEAI_TEST_OLLAMA_HOST is unset.
/// Used by the RAG eval harness and other integration tests that need a
/// live Ollama server with the embedding model installed. Skip reason is
/// set at discovery time so CI runs without Ollama report the tests as
/// skipped, not failed.
///
/// Mirrors the AdminFactAttribute pattern.
/// </summary>
public sealed class OllamaIntegrationFactAttribute : FactAttribute
{
    public OllamaIntegrationFactAttribute()
    {
        var host = Environment.GetEnvironmentVariable("FREEAI_TEST_OLLAMA_HOST");
        if (string.IsNullOrWhiteSpace(host))
        {
            Skip = "Requires FREEAI_TEST_OLLAMA_HOST pointing at a running Ollama with the embedding model installed.";
        }
    }
}
