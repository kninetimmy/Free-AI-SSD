namespace FreeAiSsd.Shared.Documents;

public sealed class DocumentIngestor
{
    private readonly DocumentLibraryManager _libraryManager;
    private readonly EmbeddingClient _embeddingClient;

    public DocumentIngestor(DocumentLibraryManager libraryManager, EmbeddingClient embeddingClient)
    {
        _libraryManager = libraryManager;
        _embeddingClient = embeddingClient;
    }

    public async Task IngestFilesAsync(
        DocumentLibraryManifest manifest,
        IEnumerable<string> sourcePaths,
        string host,
        PortableConfig config,
        Action<IndexingProgress>? progress = null,
        bool rebuildIndex = false,
        CancellationToken cancellationToken = default)
    {
        _libraryManager.EnsureLibraryFolders(manifest.Id);
        var vectorIndex = new VectorIndex(_libraryManager.GetIndexPath(manifest.Id));
        var candidates = sourcePaths.Where(DocumentParser.IsSupported).Distinct().ToList();
        var total = candidates.Count;
        var done = 0;

        foreach (var sourcePath in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            done++;
            progress?.Invoke(new IndexingProgress { TotalFiles = total, CompletedFiles = done - 1, CurrentFile = Path.GetFileName(sourcePath) });

            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var fileName = Path.GetFileName(sourcePath);
            var sha = DocumentHasher.ComputeSha256(sourcePath);
            var current = manifest.Files.FirstOrDefault(f => string.Equals(f.SourceOriginalPath, sourcePath, StringComparison.OrdinalIgnoreCase));
            if (!rebuildIndex && current is not null && string.Equals(current.Sha256, sha, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var storedFileName = $"{sha[..12]}_{fileName}";
            var storedRelativePath = Path.Combine("files", storedFileName).Replace('\\', '/');
            var storedAbsPath = Path.Combine(_libraryManager.GetLibraryPath(manifest.Id), storedRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(storedAbsPath)!);
            File.Copy(sourcePath, storedAbsPath, overwrite: true);

            var parsed = DocumentParser.Parse(storedAbsPath);
            var chunks = new List<DocumentChunk>();
            var chunkIndex = 0;
            foreach (var segment in parsed.Segments)
            {
                var texts = DocumentChunker.ChunkText(segment.Text, config.ChunkSize, config.ChunkOverlap);
                foreach (var text in texts)
                {
                    var embedding = await _embeddingClient.EmbedAsync(host, config.EmbeddingModelName, text, cancellationToken);
                    chunks.Add(new DocumentChunk
                    {
                        LibraryId = manifest.Id,
                        SourceFileName = fileName,
                        StoredRelativePath = storedRelativePath,
                        Page = segment.Page,
                        ChunkIndex = chunkIndex++,
                        Text = text,
                        TextLength = text.Length,
                        Sha256 = sha,
                        Embedding = embedding
                    });
                }
            }

            vectorIndex.UpsertFileChunks(manifest.Id, storedRelativePath, chunks);

            if (current is null)
            {
                manifest.Files.Add(new DocumentFileEntry());
                current = manifest.Files.Last();
            }

            current.SourceOriginalPath = sourcePath;
            current.StoredRelativePath = storedRelativePath;
            current.FileName = fileName;
            current.Sha256 = sha;
            current.SizeBytes = new FileInfo(sourcePath).Length;
            current.ImportedAtUtc = DateTime.UtcNow;
            current.LastModifiedUtc = File.GetLastWriteTimeUtc(sourcePath);
        }

        manifest.LastIndexedUtc = DateTime.UtcNow;
        await _libraryManager.SaveManifestAsync(manifest);
        progress?.Invoke(new IndexingProgress { TotalFiles = total, CompletedFiles = total, CurrentFile = string.Empty });
    }

    public async Task SweepFoldersAsync(DocumentLibraryManifest manifest, string host, PortableConfig config, Action<IndexingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var files = new List<string>();
        foreach (var folder in manifest.WatchedFolders.Where(Directory.Exists))
        {
            files.AddRange(Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(DocumentParser.IsSupported));
        }

        await IngestFilesAsync(manifest, files, host, config, progress, rebuildIndex: false, cancellationToken);
    }

    public async Task RebuildIndexAsync(DocumentLibraryManifest manifest, string host, PortableConfig config, Action<IndexingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var indexPath = _libraryManager.GetIndexPath(manifest.Id);
        Directory.CreateDirectory(indexPath);
        var db = Path.Combine(indexPath, "vectors.db");
        if (File.Exists(db))
        {
            File.Delete(db);
        }

        var sourceFiles = manifest.Files.Select(f => f.SourceOriginalPath).Where(File.Exists).ToList();
        await IngestFilesAsync(manifest, sourceFiles, host, config, progress, rebuildIndex: true, cancellationToken: cancellationToken);
    }

    public async Task RemoveFileAsync(DocumentLibraryManifest manifest, string storedRelativePath)
    {
        var vectorIndex = new VectorIndex(_libraryManager.GetIndexPath(manifest.Id));
        vectorIndex.RemoveFile(manifest.Id, storedRelativePath);
        var entry = manifest.Files.FirstOrDefault(f => string.Equals(f.StoredRelativePath, storedRelativePath, StringComparison.OrdinalIgnoreCase));
        if (entry is not null)
        {
            manifest.Files.Remove(entry);
            var fullPath = Path.Combine(_libraryManager.GetLibraryPath(manifest.Id), storedRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            await _libraryManager.SaveManifestAsync(manifest);
        }
    }
}
