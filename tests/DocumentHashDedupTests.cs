using FreeAiSsd.Shared.Documents;

namespace FreeAiSsd.Tests;

public class DocumentHashDedupTests
{
    [Fact]
    public void ComputeSha256_SameContent_SameHash()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rag-hash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var f1 = Path.Combine(root, "a.txt");
        var f2 = Path.Combine(root, "b.txt");
        File.WriteAllText(f1, "hello world");
        File.WriteAllText(f2, "hello world");

        var h1 = DocumentHasher.ComputeSha256(f1);
        var h2 = DocumentHasher.ComputeSha256(f2);

        Assert.Equal(h1, h2);
    }
}
