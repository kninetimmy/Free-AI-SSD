using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.PrepApp.Services;

public sealed class DriveService : IDriveService
{
    public IReadOnlyList<DriveTarget> GetCandidateDrives(bool includeFixed)
        => DriveInspector.GetCandidateDrives(includeFixed);

    public bool IsDriveEncrypted(string rootPath)
        => SsdEncryption.IsEffectivelyEncryptedForWriteGuard(rootPath);

    public bool EnsureWritable(string rootPath, string operationName, out string? blockedMessage)
    {
        var isEncrypted = SsdEncryption.IsEffectivelyEncryptedForWriteGuard(rootPath);
        if (!PrepDriveWriteGuard.IsWriteBlocked(isEncrypted))
        {
            blockedMessage = null;
            return true;
        }

        blockedMessage = PrepDriveWriteGuard.BuildBlockedOperationMessage(operationName);
        return false;
    }

    public int? GetFreeDiskSpaceGb(string rootPath)
        => SystemResources.GetFreeDiskSpaceGb(rootPath);
}
