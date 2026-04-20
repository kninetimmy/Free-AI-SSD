using System.Text.Json.Serialization;

namespace FreeAiSsd.Shared.Documents;

public sealed class DocumentLibraryRegistry
{
    public List<DocumentLibraryEntry> Libraries { get; set; } = new();
    public string? ActiveLibraryId { get; set; }
}

public sealed class DocumentLibraryEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class DocumentLibraryManifest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastIndexedUtc { get; set; }
    public List<string> WatchedFolders { get; set; } = new();
    public List<DocumentFileEntry> Files { get; set; } = new();
    /// <summary>Embedding model used for the last ingest. Hint for UI; chunks table is authoritative.</summary>
    public string? LastEmbeddingModel { get; set; }
    /// <summary>Embedding dimension used for the last ingest. Hint for UI; chunks table is authoritative.</summary>
    public int? LastEmbeddingDimension { get; set; }
}

public sealed class DocumentFileEntry
{
    public string SourceOriginalPath { get; set; } = string.Empty;
    public string StoredRelativePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime ImportedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastModifiedUtc { get; set; }
}

public sealed class DocumentChunk
{
    public string LibraryId { get; set; } = string.Empty;
    public string SourceFileName { get; set; } = string.Empty;
    public string StoredRelativePath { get; set; } = string.Empty;
    public int? Page { get; set; }
    public int ChunkIndex { get; set; }
    public string Text { get; set; } = string.Empty;
    public int TextLength { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    [JsonIgnore]
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public string EmbeddingModel { get; set; } = string.Empty;
    public int EmbeddingDimension { get; set; }
    public string ParserVersion { get; set; } = string.Empty;
    public string ChunkerVersion { get; set; } = string.Empty;
}

public sealed class RetrievalResult
{
    public DocumentChunk Chunk { get; set; } = new();
    public double Score { get; set; }
}

public sealed class ParsedDocument
{
    public List<ParsedSegment> Segments { get; set; } = new();
}

public sealed class ParsedSegment
{
    public int? Page { get; set; }
    public string Text { get; set; } = string.Empty;
}

public sealed class IndexingProgress
{
    public int TotalFiles { get; set; }
    public int CompletedFiles { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    /// <summary>Chunks successfully embedded so far for the current file.</summary>
    public int EmbeddedChunks { get; set; }
    /// <summary>Total chunks to embed for the current file.</summary>
    public int TotalChunks { get; set; }
    /// <summary>Chunks that failed to embed for the current file (non-zero only on error).</summary>
    public int FailedChunks { get; set; }
}

public sealed class RagPromptBuildResult
{
    public string Prompt { get; set; } = string.Empty;
    public List<string> Sources { get; set; } = new();
    public bool UsedContext { get; set; }
}
