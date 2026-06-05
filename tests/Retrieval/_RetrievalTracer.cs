// Manual diagnostic tool: per-query RETRIEVAL TRACE. For each entry in a local
// corpus's local_golden.json, this ingests the corpus through the real pipeline,
// embeds the query, runs the SHIPPED hybrid retriever (dense SIMD + BM25, RRF-fused
// — the product default), and dumps the top-K retrieved chunks (rank, score, file,
// page, section, snippet) with a HIT marker and the live RetrievalTopK cutoff line.
//
// Why it exists: the recall gate (RetrievalEvalHarness) only reports HIT/MISS. To
// tell a RETRIEVAL failure (the answer's chunk never surfaced — chunking/embedding)
// apart from a GENERATION failure (the right chunk surfaced but the chat model gave
// a bad/empty answer — model/prompt), you have to SEE what got retrieved. This dumps
// exactly that. If the answer chunk is in the trace but chat still failed, it's the
// model/prompt; if it's absent (or ranks past the live top-5), it's retrieval.
//
// Mirrors ChatService.PrepareRagContextAsync: same SearchHybrid call, same product
// defaults (RetrievalTopK, MinimumSimilarityThreshold, neighbor radius). It traces a
// WIDER topK (TraceTopK) than the live RetrievalTopK so you can see near-misses that
// fell just outside what chat actually feeds the model.
//
// Off by default. Enable with FREEAI_TEST_TRACE_RETRIEVAL=1 and provide:
//   FREEAI_TEST_OLLAMA_HOST=http://localhost:11434
//   FREEAI_TEST_LOCAL_CORPUS_PATH=/path/to/corpus   (folder with the PDFs + local_golden.json)
//   [FREEAI_TEST_EMBED_MODEL=nomic-embed-text]      (defaults to nomic-embed-text)
// Run:
//   dotnet test tests/FreeAiSsd.Tests.csproj --filter "FullyQualifiedName~_RetrievalTracer" --logger "console;verbosity=detailed"
// Traces are written to %TEMP%/rag-retrieval-trace/ (one .md per query + _summary.md).
// Underscore prefix marks this as a manual tool, not part of the regular suite.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace FreeAiSsd.Tests.Retrieval;

public sealed class _RetrievalTracer : IDisposable
{
    /// <summary>How many results to dump per query — wider than the live RetrievalTopK
    /// so near-misses that fell just outside the chat window are visible.</summary>
    private const int TraceTopK = 20;

    private readonly ITestOutputHelper _output;
    private readonly string _tempRoot;

    public _RetrievalTracer(ITestOutputHelper output)
    {
        _output = output;
        _tempRoot = Path.Combine(Path.GetTempPath(), $"rag-trace-ingest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Trace_LocalCorpus_Retrieval()
    {
        if (Environment.GetEnvironmentVariable("FREEAI_TEST_TRACE_RETRIEVAL") != "1")
        {
            _output.WriteLine("Skipping: set FREEAI_TEST_TRACE_RETRIEVAL=1 (plus FREEAI_TEST_OLLAMA_HOST and FREEAI_TEST_LOCAL_CORPUS_PATH) to enable.");
            return;
        }

        var host = Environment.GetEnvironmentVariable("FREEAI_TEST_OLLAMA_HOST");
        Assert.False(string.IsNullOrWhiteSpace(host), "FREEAI_TEST_OLLAMA_HOST must be set (e.g. http://localhost:11434).");

        var corpusDir = Environment.GetEnvironmentVariable("FREEAI_TEST_LOCAL_CORPUS_PATH");
        Assert.False(string.IsNullOrWhiteSpace(corpusDir), "FREEAI_TEST_LOCAL_CORPUS_PATH must point at a folder with the PDFs + local_golden.json.");
        Assert.True(Directory.Exists(corpusDir), $"Corpus dir does not exist: {corpusDir}");

        var goldenPath = Path.Combine(corpusDir!, "local_golden.json");
        Assert.True(File.Exists(goldenPath), $"Missing local_golden.json at {goldenPath}");

        var model = Environment.GetEnvironmentVariable("FREEAI_TEST_EMBED_MODEL");
        if (string.IsNullOrWhiteSpace(model)) model = "nomic-embed-text";

        var golden = JsonSerializer.Deserialize<List<GoldenEntry>>(File.ReadAllText(goldenPath), JsonOpts)
                     ?? throw new InvalidOperationException("local_golden.json parsed to null.");
        Assert.NotEmpty(golden);

        // Product defaults (match ChatService runtime, NOT the tighter gate-harness values),
        // so the trace reflects what a user actually gets in chat.
        var config = new PortableConfig
        {
            EmbeddingModelName = model!,
            MaxDocumentSizeMB = 512,
        };
        _output.WriteLine($"Retrieval config (product defaults): RetrievalTopK={config.RetrievalTopK}, " +
                          $"MinimumSimilarityThreshold={config.MinimumSimilarityThreshold}, " +
                          $"HybridRetrievalEnabled={config.HybridRetrievalEnabled}, " +
                          $"RetrievalNeighborRadius={config.RetrievalNeighborRadius}, " +
                          $"ChunkSize={config.ChunkSize}, ChunkOverlap={config.ChunkOverlap}. Tracing top-{TraceTopK}.");

        // One library, ingest every distinct fixture referenced by the golden set.
        var ssdRoot = Path.Combine(_tempRoot, "ssd");
        SsdLayout.EnsureStructure(ssdRoot);
        var manager = new DocumentLibraryManager(ssdRoot);
        var manifest = await manager.CreateLibraryAsync("trace");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var embedder = new EmbeddingClient(http);
        var ingestor = new DocumentIngestor(manager, embedder);

        var ollamaHost = StripHttpPrefix(host!);
        var fixtureFiles = golden.Select(g => Path.Combine(corpusDir!, g.Fixture)).Distinct().ToList();
        foreach (var f in fixtureFiles)
        {
            Assert.True(File.Exists(f), $"Golden references a fixture not in the corpus dir: {f}");
        }
        _output.WriteLine($"Ingesting {fixtureFiles.Count} fixture(s)... (a large guide can take a few minutes)");
        await ingestor.IngestFilesAsync(manifest, fixtureFiles, ollamaHost, config);

        var index = new VectorIndex(manager.GetIndexPath(manifest.Id));

        var traceRoot = Path.Combine(Path.GetTempPath(), "rag-retrieval-trace");
        if (Directory.Exists(traceRoot)) Directory.Delete(traceRoot, recursive: true);
        Directory.CreateDirectory(traceRoot);

        var summary = new StringBuilder();
        summary.AppendLine("# Retrieval trace summary");
        summary.AppendLine();
        summary.AppendLine($"Embed model: `{model}` · live RetrievalTopK: **{config.RetrievalTopK}** · threshold: {config.MinimumSimilarityThreshold} · traced top-{TraceTopK}");
        summary.AppendLine();
        summary.AppendLine("| Query | Verdict | Best rank | Notes |");
        summary.AppendLine("|---|---|---|---|");

        foreach (var entry in golden)
        {
            var queryEmbedding = await embedder.EmbedAsync(ollamaHost, model!, entry.Question, CancellationToken.None);
            var results = index.SearchHybrid(manifest.Id, queryEmbedding, entry.Question, TraceTopK, config.MinimumSimilarityThreshold, null);

            bool isProbe = entry.CorrectPages.Count == 0; // coverage-negative or "what-surfaces" probe — no scored hit
            int bestHitRank = -1;
            var perQuery = new StringBuilder();
            perQuery.AppendLine($"# {entry.Id}");
            perQuery.AppendLine();
            perQuery.AppendLine($"**Question:** {entry.Question}");
            perQuery.AppendLine($"**Fixture:** `{entry.Fixture}`");
            if (entry.CorrectPages.Count > 0) perQuery.AppendLine($"**Expected page(s):** {string.Join(", ", entry.CorrectPages)}");
            if (!string.IsNullOrWhiteSpace(entry.ExpectedSection)) perQuery.AppendLine($"**Expected section:** {entry.ExpectedSection}");
            if (!string.IsNullOrWhiteSpace(entry.Note)) perQuery.AppendLine($"**Note:** {entry.Note}");
            perQuery.AppendLine();
            perQuery.AppendLine($"Retrieved {results.Count} of top-{TraceTopK}. Live chat feeds only the first **{config.RetrievalTopK}** (+ neighbor radius {config.RetrievalNeighborRadius}).");
            perQuery.AppendLine();
            perQuery.AppendLine("| Rank | Score | Page | Hit | Section | Snippet |");
            perQuery.AppendLine("|---|---|---|---|---|---|");

            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                int rank = i + 1;
                bool sameFile = string.Equals(r.Chunk.SourceFileName, entry.Fixture, StringComparison.OrdinalIgnoreCase);
                bool pageHit = !isProbe && sameFile && r.Chunk.Page.HasValue && entry.CorrectPages.Contains(r.Chunk.Page.Value);
                if (pageHit && bestHitRank < 0) bestHitRank = rank;

                string hitMark = pageHit ? "✅" : (r.IsNeighbor ? "·nbr" : "");
                string cutoff = rank == config.RetrievalTopK ? " ⟵ live top-K cutoff" : "";
                perQuery.AppendLine($"| {rank}{cutoff} | {r.Score:F3} | {(r.Chunk.Page?.ToString() ?? "-")} | {hitMark} | {Trunc(r.Chunk.Section, 28)} | {Snippet(r.Chunk.Text)} |");
            }

            string verdict;
            string notes;
            if (isProbe)
            {
                verdict = "PROBE";
                notes = string.IsNullOrWhiteSpace(entry.Note) ? "no scored expectation — inspect chunks" : "see note";
            }
            else if (bestHitRank < 0)
            {
                verdict = "❌ RETRIEVAL MISS";
                notes = "expected page never retrieved → chunking/embedding/threshold";
            }
            else if (bestHitRank <= config.RetrievalTopK)
            {
                verdict = "✅ RETRIEVED (in live top-K)";
                notes = "chunk reaches the model → a bad answer is model/prompt, not retrieval";
            }
            else
            {
                verdict = "⚠️ RETRIEVED BUT BELOW CUTOFF";
                notes = $"answer ranks #{bestHitRank}, outside live top-{config.RetrievalTopK} → raise RetrievalTopK or rerank";
            }

            File.WriteAllText(Path.Combine(traceRoot, $"{entry.Id}.md"), perQuery.ToString());
            summary.AppendLine($"| {Trunc(entry.Question, 50)} | {verdict} | {(bestHitRank < 0 ? "-" : bestHitRank.ToString())} | {notes} |");
            _output.WriteLine($"{entry.Id}: {verdict} (best rank {(bestHitRank < 0 ? "none" : bestHitRank.ToString())})");
        }

        File.WriteAllText(Path.Combine(traceRoot, "_summary.md"), summary.ToString());
        _output.WriteLine($"\nTrace written to: {traceRoot}");
        _output.WriteLine("Open _summary.md first, then the per-query <id>.md files.");
    }

    private static string Snippet(string text)
    {
        var s = text.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/');
        s = string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return Trunc(s, 160);
    }

    private static string Trunc(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

    private static string StripHttpPrefix(string host)
    {
        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return host[7..];
        if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return host[8..];
        return host;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class GoldenEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("fixture")] public string Fixture { get; set; } = string.Empty;
        [JsonPropertyName("question")] public string Question { get; set; } = string.Empty;
        [JsonPropertyName("correct_pages")] public List<int> CorrectPages { get; set; } = new();
        [JsonPropertyName("expected_section")] public string? ExpectedSection { get; set; }
        /// <summary>Optional free-text note. For coverage-negative / "what surfaces" probes,
        /// leave correct_pages empty and explain the expectation here.</summary>
        [JsonPropertyName("note")] public string? Note { get; set; }
    }
}
