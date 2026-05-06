using System.Text.Json;
using System.Text.Json.Serialization;
using FreeAiSsd.Shared;

namespace FreeAiSsd.MacRunnerHost;

/// <summary>
/// Decoded init handshake the Swift mac-runner sends on the sidecar's stdin.
/// PortableConfig is parsed using the same camelCase + StringEnum policy
/// shared/<see cref="PortableConfig"/> uses for its on-disk JSON, so the
/// Swift parent can serialize the in-memory plaintext dictionary directly
/// without a translation layer. The handshake never touches disk on the
/// Mac side — that's the MAC5 plaintext invariant carried into MAC6.
/// </summary>
internal sealed record HostHandshake(string SsdRoot, string OllamaHost, PortableConfig Config)
{
    private static readonly JsonSerializerOptions HandshakeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        // No naming policy on the enum converter — allows "Installed" (PascalCase
        // emitted by PortableConfig.SaveAsync) and "installed" (Swift dictionaries
        // produced by the Mac runner) to both deserialize.
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: true) }
    };

    public static HostHandshake Parse(string json)
    {
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("ssdRoot", out var ssdRootEl) || ssdRootEl.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Init handshake missing 'ssdRoot' string.");
        }

        if (!root.TryGetProperty("ollamaHost", out var ollamaHostEl) || ollamaHostEl.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Init handshake missing 'ollamaHost' string.");
        }

        if (!root.TryGetProperty("config", out var configEl) || configEl.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Init handshake missing 'config' object.");
        }

        var ssdRoot = ssdRootEl.GetString() ?? string.Empty;
        var ollamaHost = ollamaHostEl.GetString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(ssdRoot))
        {
            throw new InvalidOperationException("Init handshake 'ssdRoot' must be non-empty.");
        }

        var config = configEl.Deserialize<PortableConfig>(HandshakeOptions)
            ?? throw new InvalidOperationException("Init handshake 'config' deserialized to null.");

        return new HostHandshake(ssdRoot, ollamaHost, config);
    }

    public static PortableConfig ParseConfig(string json)
    {
        return JsonSerializer.Deserialize<PortableConfig>(json, HandshakeOptions)
            ?? throw new InvalidOperationException("config-update payload deserialized to null.");
    }
}
