// Manual tool: dumps per-page text of each fixture under
// tests/Fixtures/RagCorpus/ to %TEMP%/rag-fixture-dump/<fixture>/page-NN.txt.
// Used to author or re-author retrieval_golden.json — handy when later
// stages change chunker behavior and existing Q/A pairs need refreshing.
// Off by default; pass FREEAI_TEST_DUMP_FIXTURES=1 to enable:
//   dotnet test --filter "FullyQualifiedName~_FixtureDumper" --logger "console;verbosity=detailed"
// Underscore prefix marks this as a manual tool, not part of the
// regular test suite.

using FreeAiSsd.Shared.Documents;
using Xunit;
using Xunit.Abstractions;

namespace FreeAiSsd.Tests.Retrieval;

public sealed class _FixtureDumper
{
    private readonly ITestOutputHelper _output;

    public _FixtureDumper(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Dump_FixtureContent_ToTempDir()
    {
        if (Environment.GetEnvironmentVariable("FREEAI_TEST_DUMP_FIXTURES") != "1")
        {
            _output.WriteLine("Skipping: set FREEAI_TEST_DUMP_FIXTURES=1 to enable.");
            return;
        }

        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "RagCorpus");
        Assert.True(Directory.Exists(fixtureDir), $"Fixture dir missing: {fixtureDir}");

        var dumpRoot = Path.Combine(Path.GetTempPath(), "rag-fixture-dump");
        if (Directory.Exists(dumpRoot)) Directory.Delete(dumpRoot, recursive: true);
        Directory.CreateDirectory(dumpRoot);

        foreach (var path in Directory.EnumerateFiles(fixtureDir, "*.*").OrderBy(p => p))
        {
            var name = Path.GetFileName(path);
            if (name.Equals("retrieval_golden.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("README.md", StringComparison.OrdinalIgnoreCase)) continue;
            if (!DocumentParser.IsSupported(path))
            {
                _output.WriteLine($"  skip (unsupported): {name}");
                continue;
            }

            var fixtureDump = Path.Combine(dumpRoot, Path.GetFileNameWithoutExtension(name));
            Directory.CreateDirectory(fixtureDump);

            var parsed = DocumentParser.Parse(path);
            _output.WriteLine($"{name}: {parsed.Segments.Count} segments");

            int pageless = 0;
            foreach (var seg in parsed.Segments)
            {
                var label = seg.Page.HasValue ? $"page-{seg.Page.Value:D3}" : $"nopage-{pageless++:D3}";
                var outPath = Path.Combine(fixtureDump, $"{label}.txt");
                File.WriteAllText(outPath, seg.Text);
            }
        }

        _output.WriteLine($"Dumps written under: {dumpRoot}");
    }
}
