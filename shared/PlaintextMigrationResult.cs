namespace FreeAiSsd.Shared;

/// <summary>
/// Result returned by <see cref="SsdEncryption.TryMigratePlaintextAsync"/>.
/// <see cref="WasPlaintextNewer"/> is true only for Branch A (plaintext absorbed into
/// encrypted); callers should replace their in-memory config with <see cref="MergedConfig"/>
/// and show the "Settings Recovery" confirmation to the user.
/// </summary>
public sealed record PlaintextMigrationResult(bool WasPlaintextNewer, PortableConfig? MergedConfig);
