using System.Text.Json;
using System.Text.Json.Serialization;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FreeAiSsd.Tests.Retrieval;

/// <summary>
/// Regression gate for the RAG pipeline overhaul. Measures recall@5 /
/// recall@20 of the shipped hybrid retriever (dense + BM25 lexical, RRF-fused
/// — the product default) against a hand-authored golden set over
/// public-domain fixtures.
///
/// Two gates land here:
///   - <c>RecallAt5_*</c> / <c>RecallAt20_*</c> — committed corpus +
///     committed baseline. Every later stage must clear the baseline
///     before merging.
///   - <c>LocalCorpus_*</c> — opt-in local-only path for real-workload
///     evaluation (e.g., Chuck's Guides). Pointed at by
///     <c>FREEAI_TEST_LOCAL_CORPUS_PATH</c>; nothing committed.
///
/// All tests skip cleanly when <c>FREEAI_TEST_OLLAMA_HOST</c> is unset.
/// </summary>
public sealed class RetrievalEvalHarness : IDisposable
{
    private readonly string _tempRoot;

    public RetrievalEvalHarness()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"rag-eval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [OllamaIntegrationFact]
    public Task RecallAt5_AgainstGoldenSet_DoesNotRegress() => RunCommittedEvalAsync(topK: 5);

    [OllamaIntegrationFact]
    public Task RecallAt20_AgainstGoldenSet_DoesNotRegress() => RunCommittedEvalAsync(topK: 20);

    [OllamaIntegrationFact]
    public Task LocalCorpus_DoesNotRegress() => RunLocalEvalAsync();

    private async Task RunCommittedEvalAsync(int topK)
    {
        var host = RequireOllamaHost();
        var model = ResolveEmbeddingModel();

        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "RagCorpus");
        var goldenPath = Path.Combine(fixtureDir, "retrieval_golden.json");
        Assert.True(File.Exists(goldenPath), $"Golden set missing at {goldenPath}. Did the csproj copy-to-output block run?");

        var golden = LoadGolden(goldenPath);
        var (recall, hitsPerQuestion) = await RunRecallAsync(host, model, fixtureDir, golden, topK);

        var repoRoot = FindRepoRoot();
        var baselineRelPath = Path.Combine("tests", "Retrieval", "baseline.json");
        var baselineAbsPath = Path.Combine(repoRoot, baselineRelPath);

        if (!File.Exists(baselineAbsPath))
        {
            // First run: write baseline and instruct the user to commit it. Fail the test so
            // a stale repo without a committed baseline never silently passes in CI.
            var seed = new BaselineRecord
            {
                RecallAt5 = topK == 5 ? recall : null,
                RecallAt20 = topK == 20 ? recall : null,
                Tolerance = 0.02,
                GeneratedAtUtc = DateTime.UtcNow,
                EmbeddingModel = model,
                FixtureCount = golden.Select(g => g.Fixture).Distinct().Count(),
                QuestionCount = golden.Count,
            };
            WriteBaseline(baselineAbsPath, seed);
            Assert.Fail(
                $"No committed baseline at {baselineRelPath}. Wrote initial seed ({nameof(BaselineRecord.RecallAt5)}={seed.RecallAt5:F3}, " +
                $"{nameof(BaselineRecord.RecallAt20)}={seed.RecallAt20:F3}). Run both tests once each, then commit the file.");
        }

        var baseline = ReadBaseline(baselineAbsPath);

        // Backfill the other metric in the existing baseline (one test populates recall@5, the other recall@20).
        bool needsUpdate = false;
        if (topK == 5 && baseline.RecallAt5 is null) { baseline.RecallAt5 = recall; needsUpdate = true; }
        if (topK == 20 && baseline.RecallAt20 is null) { baseline.RecallAt20 = recall; needsUpdate = true; }
        if (needsUpdate)
        {
            baseline.GeneratedAtUtc = DateTime.UtcNow;
            WriteBaseline(baselineAbsPath, baseline);
            Assert.Fail(
                $"Baseline at {baselineRelPath} was missing recall@{topK} — backfilled with {recall:F3}. Commit and re-run.");
        }

        var benchmark = topK == 5 ? baseline.RecallAt5!.Value : baseline.RecallAt20!.Value;
        var tolerance = baseline.Tolerance;
        var floor = benchmark - tolerance;

        Assert.True(
            recall >= floor,
            $"Recall@{topK} regressed: measured {recall:F3}, baseline {benchmark:F3}, tolerance {tolerance:F3} (floor {floor:F3}).\n" +
            "Hits per question:\n" + string.Join("\n", hitsPerQuestion.Select(h => $"  {h.Key}: {(h.Value ? "HIT" : "MISS")}")));
    }

    private async Task RunLocalEvalAsync()
    {
        RequireOllamaHost();
        var localRoot = Environment.GetEnvironmentVariable("FREEAI_TEST_LOCAL_CORPUS_PATH");
        if (string.IsNullOrWhiteSpace(localRoot))
        {
            // Not configured — skip equivalent. Use a no-op fact body so xUnit reports pass.
            // (We can't set Skip on an attribute-instance per-invocation, so test passes when env var is unset.)
            return;
        }
        Assert.True(Directory.Exists(localRoot), $"FREEAI_TEST_LOCAL_CORPUS_PATH points at non-existent dir: {localRoot}");

        var goldenPath = Path.Combine(localRoot, "local_golden.json");
        Assert.True(File.Exists(goldenPath), $"Local corpus is missing local_golden.json at {goldenPath}");

        var golden = LoadGolden(goldenPath);
        var host = RequireOllamaHost();
        var model = ResolveEmbeddingModel();

        var (recall5, _) = await RunRecallAsync(host, model, localRoot, golden, topK: 5);
        var (recall20, _) = await RunRecallAsync(host, model, localRoot, golden, topK: 20);

        var baselinePath = Path.Combine(localRoot, "local_baseline.json");
        if (!File.Exists(baselinePath))
        {
            var seed = new BaselineRecord
            {
                RecallAt5 = recall5,
                RecallAt20 = recall20,
                Tolerance = 0.02,
                GeneratedAtUtc = DateTime.UtcNow,
                EmbeddingModel = model,
                FixtureCount = golden.Select(g => g.Fixture).Distinct().Count(),
                QuestionCount = golden.Count,
            };
            WriteBaseline(baselinePath, seed);
            Assert.Fail($"No local baseline at {baselinePath} — wrote seed (R@5={recall5:F3}, R@20={recall20:F3}). Re-run to assert.");
        }

        var baseline = ReadBaseline(baselinePath);
        var floor5 = (baseline.RecallAt5 ?? 0) - baseline.Tolerance;
        var floor20 = (baseline.RecallAt20 ?? 0) - baseline.Tolerance;
        Assert.True(recall5 >= floor5, $"Local recall@5 regressed: {recall5:F3} < {floor5:F3} (baseline {baseline.RecallAt5:F3}).");
        Assert.True(recall20 >= floor20, $"Local recall@20 regressed: {recall20:F3} < {floor20:F3} (baseline {baseline.RecallAt20:F3}).");
    }

    private async Task<(double recall, Dictionary<string, bool> hitsPerQuestion)> RunRecallAsync(
        string host, string model, string fixtureDir, List<GoldenEntry> golden, int topK)
    {
        // One library per harness run. SsdLayout requires a full SSD-shaped tree; we build it
        // under the temp root so the ingest path (which validates SsdLayout) accepts it.
        var ssdRoot = Path.Combine(_tempRoot, $"ssd-topk{topK}");
        SsdLayout.EnsureStructure(ssdRoot);
        var manager = new DocumentLibraryManager(ssdRoot);
        var manifest = await manager.CreateLibraryAsync("eval");

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var embeddingClient = new EmbeddingClient(httpClient);
        var ingestor = new DocumentIngestor(manager, embeddingClient);

        var config = new PortableConfig
        {
            EmbeddingModelName = model,
            ChunkSize = 1000,
            ChunkOverlap = 100,
            // Match the product default (Stage 2C raised it 50 -> 512) so a large
            // local corpus — e.g. a 140 MB Chuck's Guide — actually ingests instead
            // of being silently rejected as oversized.
            MaxDocumentSizeMB = 512,
        };

        var ollamaHost = StripHttpPrefix(host);
        var fixtureFiles = golden.Select(g => Path.Combine(fixtureDir, g.Fixture)).Distinct().ToList();
        await ingestor.IngestFilesAsync(manifest, fixtureFiles, ollamaHost, config);

        var vectorIndex = new VectorIndex(manager.GetIndexPath(manifest.Id));

        var hits = new Dictionary<string, bool>(golden.Count);
        foreach (var entry in golden)
        {
            var queryEmbedding = await embeddingClient.EmbedAsync(ollamaHost, model, entry.Question, CancellationToken.None);
            // Measure the shipped retrieval path: hybrid (dense + BM25 lexical, RRF-fused)
            // is the product default, so the recall gate evaluates what users actually get.
            var results = vectorIndex.SearchHybrid(
                manifest.Id, queryEmbedding, entry.Question, topK, config.MinimumSimilarityThreshold, null);
            // Empty CorrectPages → filename-only match (for non-paginated formats like .md/.txt
            // where DocumentParser produces a single segment with Page=null). A non-empty
            // expected_section further requires the retrieved chunk to land in that section.
            bool hit = results.Any(r =>
                string.Equals(r.Chunk.SourceFileName, entry.Fixture, StringComparison.OrdinalIgnoreCase) &&
                (entry.CorrectPages.Count == 0
                 || (r.Chunk.Page.HasValue && entry.CorrectPages.Contains(r.Chunk.Page.Value))) &&
                SectionMatches(entry.ExpectedSection, r.Chunk));
            hits[entry.Id] = hit;
        }

        var recall = hits.Count == 0 ? 0.0 : (double)hits.Values.Count(v => v) / hits.Count;
        return (recall, hits);
    }

    /// <summary>
    /// True when a golden entry's optional <c>expected_section</c> constraint is satisfied by a
    /// retrieved chunk. An empty/absent constraint always passes (back-compat with Stage 1
    /// entries that predate section metadata). The match is a case-insensitive substring test
    /// against the chunk's heading breadcrumb — which ends with the leaf section — and, as a
    /// fallback, the leaf section alone, so an author can name either a parent heading or the
    /// leaf section.
    /// </summary>
    internal static bool SectionMatches(string? expectedSection, DocumentChunk chunk)
    {
        if (string.IsNullOrWhiteSpace(expectedSection)) return true;
        var needle = expectedSection.Trim();
        return chunk.HeadingPath.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || chunk.Section.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static string RequireOllamaHost()
    {
        var host = Environment.GetEnvironmentVariable("FREEAI_TEST_OLLAMA_HOST");
        Assert.False(string.IsNullOrWhiteSpace(host), "FREEAI_TEST_OLLAMA_HOST must be set when this test is not skipped.");
        return host!;
    }

    private static string ResolveEmbeddingModel()
    {
        var fromEnv = Environment.GetEnvironmentVariable("FREEAI_TEST_EMBED_MODEL");
        return string.IsNullOrWhiteSpace(fromEnv) ? "nomic-embed-text" : fromEnv!;
    }

    private static string StripHttpPrefix(string host)
    {
        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return host[7..];
        if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return host[8..];
        return host;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FreeAiSsd.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException(
                $"Could not locate repo root from {AppContext.BaseDirectory} (no FreeAiSsd.sln found in any parent).");
        }
        return dir.FullName;
    }

    private static List<GoldenEntry> LoadGolden(string path)
    {
        var json = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize<List<GoldenEntry>>(json, GoldenJsonOptions)
            ?? throw new InvalidOperationException($"Golden set at {path} parsed to null.");
        Assert.NotEmpty(entries);
        return entries;
    }

    private static BaselineRecord ReadBaseline(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BaselineRecord>(json, GoldenJsonOptions)
            ?? throw new InvalidOperationException($"Baseline at {path} parsed to null.");
    }

    private static void WriteBaseline(string path, BaselineRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(record, GoldenJsonOptions);
        File.WriteAllText(path, json);
    }

    private static readonly JsonSerializerOptions GoldenJsonOptions = new()
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
        /// <summary>
        /// Optional Stage 2 section constraint. When set, a retrieved chunk only counts as a hit
        /// if its section attribution also satisfies <see cref="SectionMatches"/>. Absent/empty
        /// means no section constraint (Stage 1 entries behave exactly as before).
        /// </summary>
        [JsonPropertyName("expected_section")] public string? ExpectedSection { get; set; }
    }

    private sealed class BaselineRecord
    {
        [JsonPropertyName("recall_at_5")] public double? RecallAt5 { get; set; }
        [JsonPropertyName("recall_at_20")] public double? RecallAt20 { get; set; }
        [JsonPropertyName("tolerance")] public double Tolerance { get; set; } = 0.02;
        [JsonPropertyName("generated_at_utc")] public DateTime GeneratedAtUtc { get; set; }
        [JsonPropertyName("embedding_model")] public string EmbeddingModel { get; set; } = string.Empty;
        [JsonPropertyName("fixture_count")] public int FixtureCount { get; set; }
        [JsonPropertyName("question_count")] public int QuestionCount { get; set; }
    }
}
