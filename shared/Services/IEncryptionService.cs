namespace FreeAiSsd.Shared.Services;

public interface IEncryptionService
{
    bool IsEncryptionEnabled(string rootPath);
    Task EnableConfigEncryptionAsync(string root, string configPath, string passphrase);
    Task<UnlockMaterial> EnableConfigEncryptionAsync(string root, PortableConfig config, string passphrase, CancellationToken ct = default);
}
