namespace FreeAiSsd.Shared.Services;

public interface IDriveService
{
    IReadOnlyList<DriveTarget> GetCandidateDrives(bool includeFixed);
    bool IsDriveEncrypted(string rootPath);
    bool EnsureWritable(string rootPath, string operationName, out string? blockedMessage);
    int? GetFreeDiskSpaceGb(string rootPath);
    void EnsureSsdStructure(string rootPath);

    /// <summary>
    /// Formats the volume at <paramref name="rootPath"/>. DESTRUCTIVE — all data
    /// on the target volume is erased. Caller is responsible for user confirmation
    /// and admin elevation. Throws on non-zero exit from the format command.
    /// </summary>
    Task FormatAsync(string rootPath, string label, string fileSystem, Action<string>? onOutput, CancellationToken ct);
}
