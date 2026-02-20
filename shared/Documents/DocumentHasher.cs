using System.Security.Cryptography;

namespace FreeAiSsd.Shared.Documents;

public static class DocumentHasher
{
    public static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
