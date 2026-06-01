// Manual tool: dumps per-page text + a heading outline of each fixture to
// %TEMP%/rag-fixture-dump/<fixture>/. Used to author or re-author golden
// sets (retrieval_golden.json and local_golden.json) — handy when later
// stages change chunker behavior and existing Q/A pairs need refreshing.
// Off by default; pass FREEAI_TEST_DUMP_FIXTURES=1 to enable:
//   dotnet test --filter "FullyQualifiedName~_FixtureDumper" --logger "console;verbosity=detailed"
// Dumps the committed corpus under tests/Fixtures/RagCorpus/ and, when
// FREEAI_TEST_LOCAL_CORPUS_PATH is set, the local corpus too (for authoring
// local_golden.json against private material like a Chuck's Guide).
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

        var dumpRoot = Path.Combine(Path.GetTempPath(), "rag-fixture-dump");
        if (Directory.Exists(dumpRoot)) Directory.Delete(dumpRoot, recursive: true);
        Directory.CreateDirectory(dumpRoot);

        var committedDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "RagCorpus");
        Assert.True(Directory.Exists(committedDir), $"Fixture dir missing: {committedDir}");
        DumpDirectory(committedDir, dumpRoot, "retrieval_golden.json", "README.md");

        var localDir = Environment.GetEnvironmentVariable("FREEAI_TEST_LOCAL_CORPUS_PATH");
        if (!string.IsNullOrWhiteSpace(localDir) && Directory.Exists(localDir))
        {
            _output.WriteLine($"Local corpus: {localDir}");
            DumpDirectory(localDir, dumpRoot, "local_golden.json", "local_baseline.json");
        }

        _output.WriteLine($"Dumps written under: {dumpRoot}");
    }

    private void DumpDirectory(string sourceDir, string dumpRoot, params string[] skipNames)
    {
        foreach (var path in Directory.EnumerateFiles(sourceDir, "*.*").OrderBy(p => p))
        {
            var name = Path.GetFileName(path);
            if (skipNames.Any(s => name.Equals(s, StringComparison.OrdinalIgnoreCase))) continue;
            if (!DocumentParser.IsSupported(path))
            {
                _output.WriteLine($"  skip (unsupported): {name}");
                continue;
            }

            var fixtureDump = Path.Combine(dumpRoot, Path.GetFileNameWithoutExtension(name));
            Directory.CreateDirectory(fixtureDump);

            var parsed = DocumentParser.Parse(path);
            _output.WriteLine($"{name}: {parsed.Segments.Count} segments, {parsed.Blocks.Count} blocks");

            int pageless = 0;
            foreach (var seg in parsed.Segments)
            {
                var label = seg.Page.HasValue ? $"page-{seg.Page.Value:D4}" : $"nopage-{pageless++:D3}";
                File.WriteAllText(Path.Combine(fixtureDump, $"{label}.txt"), seg.Text);
            }

            // Heading outline: the detected section structure, one heading per line.
            // This is the map for choosing questions and authoring expected_section.
            var outline = parsed.Blocks
                .Where(b => b.Kind == BlockKind.Heading)
                .Select(b => $"[p{(b.Page?.ToString() ?? "-")}] {new string(' ', Math.Max(0, (b.HeadingLevel - 1) * 2))}{b.Text}");
            File.WriteAllText(Path.Combine(fixtureDump, "_outline.txt"), string.Join(Environment.NewLine, outline));
        }
    }
}
