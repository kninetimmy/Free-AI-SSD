using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.PrepApp.Services;

public sealed class EncryptionService : IEncryptionService
{
    public bool IsEncryptionEnabled(string rootPath)
        => SsdEncryption.IsEffectivelyEncryptedForWriteGuard(rootPath);

    public async Task EnableConfigEncryptionAsync(string root, string configPath, string passphrase)
        => await SsdEncryption.EnableConfigEncryptionAsync(root, configPath, passphrase);
}
