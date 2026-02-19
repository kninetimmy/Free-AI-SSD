namespace FreeAiSsd.Shared.Services;

public interface IDriveService
{
    IReadOnlyList<DriveTarget> GetCandidateDrives(bool includeFixed);
    bool IsDriveEncrypted(string rootPath);
    bool EnsureWritable(string rootPath, string operationName, out string? blockedMessage);
    int? GetFreeDiskSpaceGb(string rootPath);
    void EnsureSsdStructure(string rootPath);
}
