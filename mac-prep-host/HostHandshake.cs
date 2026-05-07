using System.Text.Json;

namespace FreeAiSsd.MacPrepHost;

/// <summary>
/// Decoded init handshake the Swift mac-prep-app sends on the sidecar's
/// stdin. Smaller surface than mac-runner-host's handshake — prep is a
/// one-shot flow with no long-running HTTP API and no PortableConfig
/// crossing the language boundary (Swift owns encrypted-config IO via
/// SsdEncryption.swift, MAC5 invariant).
///
/// Fields:
///   ssdRoot     (required) — drive root the prep flow targets
///   ollamaHost  (optional) — Ollama base URL, defaults to
///                            http://127.0.0.1:11434 (matches the Mac
///                            Runner Ollama lifecycle service binding)
/// </summary>
internal sealed record HostHandshake(string SsdRoot, string OllamaHost)
{
    public const string DefaultOllamaHost = "http://127.0.0.1:11434";

    public static HostHandshake Parse(string json)
    {
        // JsonDocument owns pooled buffers — dispose explicitly so a malformed
        // handshake doesn't leak ArrayPool slots in the host's startup path.
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("ssdRoot", out var ssdRootEl) || ssdRootEl.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Init handshake missing 'ssdRoot' string.");
        }

        var ssdRoot = ssdRootEl.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ssdRoot))
        {
            throw new InvalidOperationException("Init handshake 'ssdRoot' must be non-empty.");
        }

        var ollamaHost = DefaultOllamaHost;
        if (root.TryGetProperty("ollamaHost", out var ollamaEl) && ollamaEl.ValueKind == JsonValueKind.String)
        {
            var candidate = ollamaEl.GetString();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                ollamaHost = candidate;
            }
        }

        return new HostHandshake(ssdRoot, ollamaHost);
    }
}
