using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.MacRunnerHost;

/// <summary>
/// Mac sidecar IConfigStore that performs no disk IO. The MAC5/MAC6 invariant
/// keeps encrypted-config IO Swift-authoritative — the sidecar holds an
/// in-memory <see cref="PortableConfig"/> received via stdin handshake but
/// never writes it back. MAC8 introduces call paths through
/// <see cref="DocumentOperationsService.SaveConfigAsync"/> that would
/// otherwise try to persist via <see cref="ConfigStore"/>; this no-op
/// satisfies the contract while letting the in-memory mutation flow through
/// to subsequent /api/chat requests. Mutating /api/library endpoints return
/// the new activeLibraryId in their response so the Swift parent can save
/// via SsdEncryption.swift.
/// </summary>
internal sealed class NoOpConfigStore : IConfigStore
{
    public bool IsSessionUnlocked => false;

    public Task<PortableConfig?> LoadAsync(string ssdRoot, CancellationToken ct)
    {
        // The host receives PortableConfig via stdin handshake, not from disk.
        // Return null so any accidental load attempt fails closed rather than
        // silently producing an empty config.
        return Task.FromResult<PortableConfig?>(null);
    }

    public Task SaveAsync(string ssdRoot, PortableConfig config, CancellationToken ct)
    {
        // Intentional no-op. Swift owns config persistence on Mac.
        return Task.CompletedTask;
    }

    public void UnlockSession(UnlockMaterial material)
    {
        // No-op: the host has no derived key and never will. The Swift parent
        // never calls this on the Mac path — the handshake hands a fully-formed
        // PortableConfig over stdin instead.
    }

    public Task FlushAsync(TimeSpan timeout) => Task.CompletedTask;

    public void LockSession()
    {
        // No-op: nothing cached to clear.
    }
}
