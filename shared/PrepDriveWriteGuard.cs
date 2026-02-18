namespace FreeAiSsd.Shared;

public static class PrepDriveWriteGuard
{
    public const string ReadOnlyReason =
        "Encrypted drive detected. PrepApp currently supports read-only access for encrypted SSDs.";

    public static bool IsWriteBlocked(bool isEncryptedDrive) => isEncryptedDrive;

    public static string BuildBlockedOperationMessage(string operationName)
    {
        var normalizedOperation = string.IsNullOrWhiteSpace(operationName)
            ? "Operation"
            : operationName.Trim();
        return $"{normalizedOperation} blocked: {ReadOnlyReason}";
    }
}
