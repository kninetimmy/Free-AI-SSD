namespace FreeAiSsd.Shared;

/// <summary>
/// Safety guard that prevents the PrepApp from writing to encrypted SSDs.
/// Once a drive is encrypted, PrepApp treats it as read-only — only the Runner
/// (with the user's password) can unlock and modify encrypted drives.
/// This avoids accidental corruption of encrypted data.
/// </summary>
public static class PrepDriveWriteGuard
{
    /// <summary>User-facing explanation for why write operations are blocked.</summary>
    public const string ReadOnlyReason =
        "Encrypted drive detected. PrepApp currently supports read-only access for encrypted SSDs.";

    /// <summary>
    /// Checks whether write operations should be blocked for the given SSD root
    /// by querying the encryption state on disk.
    /// </summary>
    /// <param name="ssdRoot">Root path of the SSD to check.</param>
    /// <returns>True if writes should be blocked (drive is encrypted).</returns>
    public static bool IsWriteBlocked(string ssdRoot) =>
        SsdEncryption.IsEffectivelyEncryptedForWriteGuard(ssdRoot);

    /// <summary>
    /// Overload that accepts a pre-computed encryption state flag,
    /// used when the UI has already determined the encryption status.
    /// </summary>
    public static bool IsWriteBlocked(bool isEncryptedDrive) => isEncryptedDrive;

    /// <summary>
    /// Builds a user-facing message explaining why a specific operation was blocked.
    /// Falls back to a generic "Operation" prefix if the operation name is empty.
    /// </summary>
    /// <param name="operationName">The name of the blocked operation (e.g., "Finalize", "Pull model").</param>
    /// <returns>Formatted blocked-operation message string.</returns>
    public static string BuildBlockedOperationMessage(string operationName)
    {
        var normalizedOperation = string.IsNullOrWhiteSpace(operationName)
            ? "Operation"
            : operationName.Trim();
        return $"{normalizedOperation} blocked: {ReadOnlyReason}";
    }
}
