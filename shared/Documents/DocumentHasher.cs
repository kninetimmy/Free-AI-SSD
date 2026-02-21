using FreeAiSsd.Shared.Helpers;

namespace FreeAiSsd.Shared.Documents;

public static class DocumentHasher
{
    public static string ComputeSha256(string filePath) =>
        CryptoUtils.ComputeSha256Hex(filePath);
}
