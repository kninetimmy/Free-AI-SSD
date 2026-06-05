using System.Net;

namespace FreeAiSsd.Shared.Discovery;

/// <summary>
/// Shared constants and pure selection logic for Runner LAN discovery. The
/// socket-bound broadcaster/listener live alongside; this type holds the parts
/// that are deterministic and unit-testable without touching the network.
/// </summary>
public static class RunnerDiscovery
{
    /// <summary>
    /// Administratively-scoped (239.0.0.0/8, org-local) multicast group for the
    /// Runner beacon. Distinct from the API port so discovery and traffic never
    /// collide.
    /// </summary>
    public static readonly IPAddress MulticastGroup = IPAddress.Parse("239.255.41.55");

    /// <summary>UDP port the beacon is multicast on.</summary>
    public const int MulticastPort = 41556;

    /// <summary>How often the Runner re-emits its beacon.</summary>
    public static readonly TimeSpan BroadcastInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A Runner seen on the LAN. <see cref="Host"/> comes from the UDP packet's
    /// source address (not the payload), so it is always the reachable address.
    /// </summary>
    public sealed record DiscoveredRunner(string Host, int Port, string Name, string Fingerprint);

    /// <summary>
    /// Picks the Runner a companion should auto-connect to. Prefers an exact
    /// API-key fingerprint match (so a companion finds *its own* Runner even when
    /// several are advertising); falls back to the sole runner on the LAN when
    /// nothing matches the key. Returns null when the choice is ambiguous (no
    /// fingerprint match and more than one candidate) so the caller prompts.
    /// </summary>
    public static DiscoveredRunner? SelectBestMatch(
        IReadOnlyList<DiscoveredRunner> candidates,
        string? myFingerprint)
    {
        if (candidates is null || candidates.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(myFingerprint))
        {
            var matches = candidates
                .Where(c => string.Equals(c.Fingerprint, myFingerprint, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count >= 1)
            {
                // One key, one drive: a fingerprint match is unambiguous even if
                // duplicates arrive from multiple NICs — take the first.
                return matches[0];
            }
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }
}
