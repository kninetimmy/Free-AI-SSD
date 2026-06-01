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
    /// <summary>Title of the section this chunk belongs to (deepest heading). Empty when unknown.</summary>
    public string Section { get; set; } = string.Empty;
    /// <summary>Breadcrumb of headings down to <see cref="Section"/>, e.g. "Engines &gt; Start". Empty when unknown.</summary>
    public string HeadingPath { get; set; } = string.Empty;
    /// <summary>Start offset of this chunk within its page text (or whole-file text for non-paginated formats).</summary>
    public int CharOffsetStart { get; set; }
    /// <summary>End offset (exclusive) of this chunk within its page text (or whole-file text for non-paginated formats).</summary>
    public int CharOffsetEnd { get; set; }
    /// <summary>Content kind. "text" for all Stage 2 chunks; "table"/"image_ref" reserved for future work.</summary>
    public string ContentType { get; set; } = "text";
}

public sealed class RetrievalResult
{
    public DocumentChunk Chunk { get; set; } = new();
    public double Score { get; set; }
}

public sealed class ParsedDocument
{
    /// <summary>Flat per-page (PDF) or whole-file (text) text. Retained for back-compat consumers.</summary>
    public List<ParsedSegment> Segments { get; set; } = new();

    /// <summary>Ordered structural blocks (headings + body) used by section-aware chunking.</summary>
    public List<DocumentBlock> Blocks { get; set; } = new();
}

public sealed class ParsedSegment
{
    public int? Page { get; set; }
    public string Text { get; set; } = string.Empty;
}

public enum BlockKind
{
    Heading,
    Body,
}

/// <summary>An ordered structural unit of a parsed document — a heading line or a run of body text.</summary>
public sealed class DocumentBlock
{
    /// <summary>Source page for paginated formats (PDF); null for whole-file formats (markdown, txt).</summary>
    public int? Page { get; set; }
    public BlockKind Kind { get; set; }
    /// <summary>Heading depth (1 = top level). Only meaningful when <see cref="Kind"/> is <see cref="BlockKind.Heading"/>.</summary>
    public int HeadingLevel { get; set; }
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// One extracted line of a PDF with the typographic signals heading detection relies on.
/// Public so the font-tier classifier can be exercised as a pure function over synthetic
/// input, without needing a PDF writer in tests.
/// </summary>
public sealed class LineRecord
{
    public int Page { get; set; }
    public string Text { get; set; } = string.Empty;
    /// <summary>Representative (char-weighted mode) font size of the line, in points.</summary>
    public double FontSize { get; set; }
    /// <summary>True when the majority of the line's characters use a bold font face.</summary>
    public bool IsBold { get; set; }
}

/// <summary>
/// A chunk emitted by section-aware chunking, carrying the section attribution and
/// page-relative character offsets that <see cref="DocumentChunk"/> persists.
/// </summary>
public sealed class DocumentChunkSpan
{
    public string Text { get; set; } = string.Empty;
    public int? Page { get; set; }
    public string Section { get; set; } = string.Empty;
    public string HeadingPath { get; set; } = string.Empty;
    public int CharOffsetStart { get; set; }
    public int CharOffsetEnd { get; set; }
    public string ContentType { get; set; } = "text";
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
