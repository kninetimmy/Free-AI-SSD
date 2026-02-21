using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;
using Microsoft.Data.Sqlite;

namespace FreeAiSsd.Tests;

/// <summary>
/// Integration tests for the full RAG pipeline: Ingest → Search roundtrip.
/// Uses a fake HTTP handler to return deterministic embeddings so no Ollama server is needed.
/// </summary>
public class RagPipelineIntegrationTests : IDisposable
{
    private readonly string _tempRoot;

    public RagPipelineIntegrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"rag-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        // SQLite's connection pool keeps the .db file locked on Windows even after
        // individual connections are disposed. Clear all pooled connections before
        // attempting to delete the temp directory.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task IngestAndSearch_WithRelevantQuery_ReturnsResultsAboveThreshold()
    {
        // Arrange: create SSD layout, library, and a test document
        var ssdRoot = Path.Combine(_tempRoot, "ssd");
        SsdLayout.EnsureStructure(ssdRoot);
        var manager = new DocumentLibraryManager(ssdRoot);
        var manifest = await manager.CreateLibraryAsync("test-lib");

        var docPath = Path.Combine(_tempRoot, "photosynthesis.txt");
        File.WriteAllText(docPath, "Photosynthesis is the process by which plants convert sunlight into energy.");

        var handler = new KeywordEmbeddingHandler("photosynthesis");
        using var httpClient = new HttpClient(handler);
        var embeddingClient = new EmbeddingClient(httpClient);
        var ingestor = new DocumentIngestor(manager, embeddingClient);

        var config = new PortableConfig { ChunkSize = 500, ChunkOverlap = 50, MaxDocumentSizeMB = 50 };

        // Act: ingest the document
        await ingestor.IngestFilesAsync(manifest, new[] { docPath }, "localhost:11434", config);

        // Embed the query using the same mock
        var queryEmbedding = await embeddingClient.EmbedAsync("localhost:11434", "nomic-embed-text", "photosynthesis", CancellationToken.None);

        // Search the vector index
        var vectorIndex = new VectorIndex(manager.GetIndexPath(manifest.Id));
        var results = vectorIndex.Search(manifest.Id, queryEmbedding, topK: 5, minimumSimilarity: 0.3, logger: null);

        // Assert
        Assert.NotEmpty(results);
        Assert.True(results[0].Score > 0.5, $"Expected score > 0.5 for relevant query, got {results[0].Score}");
        Assert.Contains("Photosynthesis", results[0].Chunk.Text);
    }

    [Fact]
    public async Task IngestAndSearch_WithIrrelevantQuery_ReturnsNoResultsAboveThreshold()
    {
        // Arrange
        var ssdRoot = Path.Combine(_tempRoot, "ssd2");
        SsdLayout.EnsureStructure(ssdRoot);
        var manager = new DocumentLibraryManager(ssdRoot);
        var manifest = await manager.CreateLibraryAsync("test-lib");

        var docPath = Path.Combine(_tempRoot, "photosynthesis2.txt");
        File.WriteAllText(docPath, "Photosynthesis is the process by which plants convert sunlight into energy.");

        var handler = new KeywordEmbeddingHandler("photosynthesis");
        using var httpClient = new HttpClient(handler);
        var embeddingClient = new EmbeddingClient(httpClient);
        var ingestor = new DocumentIngestor(manager, embeddingClient);

        var config = new PortableConfig { ChunkSize = 500, ChunkOverlap = 50, MaxDocumentSizeMB = 50 };

        // Act: ingest, then search for something completely unrelated
        await ingestor.IngestFilesAsync(manifest, new[] { docPath }, "localhost:11434", config);

        var queryEmbedding = await embeddingClient.EmbedAsync("localhost:11434", "nomic-embed-text", "quantum mechanics", CancellationToken.None);

        var vectorIndex = new VectorIndex(manager.GetIndexPath(manifest.Id));
        var results = vectorIndex.Search(manifest.Id, queryEmbedding, topK: 5, minimumSimilarity: 0.5, logger: null);

        // Assert: irrelevant query should return no results above threshold
        Assert.Empty(results);
    }

    [Fact]
    public async Task IngestAndSearch_MultipleDocuments_ReturnsCorrectDocument()
    {
        // Arrange
        var ssdRoot = Path.Combine(_tempRoot, "ssd3");
        SsdLayout.EnsureStructure(ssdRoot);
        var manager = new DocumentLibraryManager(ssdRoot);
        var manifest = await manager.CreateLibraryAsync("test-lib");

        var docA = Path.Combine(_tempRoot, "biology.txt");
        File.WriteAllText(docA, "Photosynthesis is the process by which plants convert sunlight into energy using chlorophyll.");

        var docB = Path.Combine(_tempRoot, "cooking.txt");
        File.WriteAllText(docB, "To make pasta, boil water and add salt before cooking the noodles.");

        var handler = new KeywordEmbeddingHandler("photosynthesis");
        using var httpClient = new HttpClient(handler);
        var embeddingClient = new EmbeddingClient(httpClient);
        var ingestor = new DocumentIngestor(manager, embeddingClient);

        var config = new PortableConfig { ChunkSize = 500, ChunkOverlap = 50, MaxDocumentSizeMB = 50 };

        // Act
        await ingestor.IngestFilesAsync(manifest, new[] { docA, docB }, "localhost:11434", config);

        var queryEmbedding = await embeddingClient.EmbedAsync("localhost:11434", "nomic-embed-text", "photosynthesis", CancellationToken.None);

        var vectorIndex = new VectorIndex(manager.GetIndexPath(manifest.Id));
        var results = vectorIndex.Search(manifest.Id, queryEmbedding, topK: 5, minimumSimilarity: 0.3, logger: null);

        // Assert: the biology document should rank first
        Assert.NotEmpty(results);
        Assert.Equal("biology.txt", results[0].Chunk.SourceFileName);
    }

    [Fact]
    public async Task IngestAndSearch_UnsupportedFileType_IsSkipped()
    {
        // Arrange
        var ssdRoot = Path.Combine(_tempRoot, "ssd4");
        SsdLayout.EnsureStructure(ssdRoot);
        var manager = new DocumentLibraryManager(ssdRoot);
        var manifest = await manager.CreateLibraryAsync("test-lib");

        var unsupportedFile = Path.Combine(_tempRoot, "image.png");
        File.WriteAllBytes(unsupportedFile, new byte[] { 0x89, 0x50, 0x4E, 0x47 }); // PNG header

        var handler = new KeywordEmbeddingHandler("test");
        using var httpClient = new HttpClient(handler);
        var embeddingClient = new EmbeddingClient(httpClient);
        var ingestor = new DocumentIngestor(manager, embeddingClient);

        var config = new PortableConfig { MaxDocumentSizeMB = 50 };

        // Act
        await ingestor.IngestFilesAsync(manifest, new[] { unsupportedFile }, "localhost:11434", config);

        // Assert: no files should have been ingested
        var vectorIndex = new VectorIndex(manager.GetIndexPath(manifest.Id));
        Assert.Equal(0, vectorIndex.GetChunkCount(manifest.Id));
    }

    [Fact]
    public async Task RemoveFile_AfterIngestion_RemovesFromIndex()
    {
        // Arrange
        var ssdRoot = Path.Combine(_tempRoot, "ssd5");
        SsdLayout.EnsureStructure(ssdRoot);
        var manager = new DocumentLibraryManager(ssdRoot);
        var manifest = await manager.CreateLibraryAsync("test-lib");

        var docPath = Path.Combine(_tempRoot, "removable.txt");
        File.WriteAllText(docPath, "This document will be removed from the library after ingestion.");

        var handler = new KeywordEmbeddingHandler("removed");
        using var httpClient = new HttpClient(handler);
        var embeddingClient = new EmbeddingClient(httpClient);
        var ingestor = new DocumentIngestor(manager, embeddingClient);

        var config = new PortableConfig { ChunkSize = 500, ChunkOverlap = 50, MaxDocumentSizeMB = 50 };

        await ingestor.IngestFilesAsync(manifest, new[] { docPath }, "localhost:11434", config);

        var vectorIndex = new VectorIndex(manager.GetIndexPath(manifest.Id));
        Assert.True(vectorIndex.GetChunkCount(manifest.Id) > 0);

        // Act: remove the file
        var fileEntry = manifest.Files.First();
        await ingestor.RemoveFileAsync(manifest, fileEntry.StoredRelativePath);

        // Assert: index should be empty
        Assert.Equal(0, vectorIndex.GetChunkCount(manifest.Id));
    }

    /// <summary>
    /// Fake HTTP handler that returns deterministic embeddings based on whether
    /// the input text contains a target keyword. Texts containing the keyword get
    /// an embedding pointing in one direction; others point in an orthogonal direction.
    /// This allows testing similarity search without a real embedding model.
    /// </summary>
    private sealed class KeywordEmbeddingHandler : HttpMessageHandler
    {
        private readonly string _keyword;
        private const int Dimensions = 64;

        public KeywordEmbeddingHandler(string keyword)
        {
            _keyword = keyword;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var json = JsonSerializer.Deserialize<JsonElement>(body);
            var input = json.GetProperty("input").GetString() ?? "";

            var embedding = input.Contains(_keyword, StringComparison.OrdinalIgnoreCase)
                ? CreateDirectionA()
                : CreateDirectionB();

            var responseBody = new { embeddings = new[] { embedding } };
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(responseBody)
            };
            return response;
        }

        private static float[] CreateDirectionA()
        {
            var v = new float[Dimensions];
            v[0] = 1f;
            return v;
        }

        private static float[] CreateDirectionB()
        {
            var v = new float[Dimensions];
            v[1] = 1f;
            return v;
        }
    }
}
