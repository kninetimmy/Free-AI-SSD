using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeAiSsd.Shared.Discovery;

/// <summary>
/// The payload a Runner periodically multicasts so a companion on the same LAN
/// can find it without a hand-typed IP. Carries only host:port-discovery data
/// plus a non-reversible fingerprint of the API key — never the key itself, and
/// not the IP (the companion reads that from the UDP packet's source address).
/// </summary>
public sealed record RunnerBeacon(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("fp")] string Fingerprint)
{
    /// <summary>Service tag every beacon carries so foreign multicast traffic is ignored.</summary>
    public const string ServiceTag = "freeaissd-runner";

    /// <summary>Wire schema version. Bump on a breaking payload change.</summary>
    public const int CurrentVersion = 1;

    [JsonPropertyName("service")]
    public string Service { get; init; } = ServiceTag;

    [JsonPropertyName("v")]
    public int Version { get; init; } = CurrentVersion;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    /// <summary>
    /// Parses a received datagram, returning false (never throwing) for malformed
    /// payloads, foreign services, unknown versions, or out-of-range ports so the
    /// listen loop can simply skip junk on the wire.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> datagram, out RunnerBeacon? beacon)
    {
        beacon = null;
        try
        {
            var parsed = JsonSerializer.Deserialize<RunnerBeacon>(datagram, JsonOptions);
            if (parsed is null
                || !string.Equals(parsed.Service, ServiceTag, StringComparison.Ordinal)
                || parsed.Version != CurrentVersion
                || parsed.Port is < 1 or > 65535)
            {
                return false;
            }

            beacon = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Non-reversible fingerprint of the API key (first 16 bytes of SHA-256, hex).
    /// Lets a companion auto-match the right Runner to its prepped key without the
    /// key ever crossing the wire. Empty string when there is no key.
    /// </summary>
    public static string ComputeFingerprint(string? apiKey)
    {
        var trimmed = apiKey?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return string.Empty;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(trimmed));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }
}
