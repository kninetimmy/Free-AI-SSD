namespace FreeAiSsd.Shared.Documents;

public sealed class DocumentIngestor
{
    private const double MaxEmbeddingFailureRatioBeforeAbort = 0.50d;

    private readonly DocumentLibraryManager _libraryManager;
    private readonly EmbeddingClient _embeddingClient;
    private readonly SsdLogger? _logger;

    public DocumentIngestor(DocumentLibraryManager libraryManager, EmbeddingClient embeddingClient, SsdLogger? logger = null)
    {
        _libraryManager = libraryManager;
        _embeddingClient = embeddingClient;
        _logger = logger;
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
        var maxSizeBytes = (long)config.MaxDocumentSizeMB * 1024 * 1024;
        var perFileErrors = new List<Exception>();

        foreach (var sourcePath in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            done++;
            progress?.Invoke(new IndexingProgress { TotalFiles = total, CompletedFiles = done - 1, CurrentFile = Path.GetFileName(sourcePath) });

            if (!File.Exists(sourcePath))
            {
                continue;
            }

            // --- Security: Symlink / reparse-point detection ---
            var fileAttributes = File.GetAttributes(sourcePath);
            if ((fileAttributes & FileAttributes.ReparsePoint) != 0)
            {
                _logger?.Warn($"Rejected symlink/reparse point: {sourcePath}");
                continue;
            }

            // --- Security: File size limit ---
            var fileSize = new FileInfo(sourcePath).Length;
            if (fileSize > maxSizeBytes)
            {
                _logger?.Warn($"Rejected oversized file ({fileSize / (1024.0 * 1024.0):F1} MB exceeds {config.MaxDocumentSizeMB} MB limit): {sourcePath}");
                continue;
            }

            var fileName = Path.GetFileName(sourcePath);
            var sha = DocumentHasher.ComputeSha256(sourcePath);
            var current = manifest.Files.FirstOrDefault(f => string.Equals(f.SourceOriginalPath, sourcePath, StringComparison.OrdinalIgnoreCase));

            // Rename detection: same sha at a new path → update manifest, skip re-embed.
            // Only when exactly one manifest entry matches the sha (>1 = duplicates, fall through to new entry).
            if (!rebuildIndex && current is null)
            {
                var shaMatches = manifest.Files
                    .Where(f => string.Equals(f.Sha256, sha, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (shaMatches.Count == 1)
                {
                    shaMatches[0].SourceOriginalPath = sourcePath;
                    shaMatches[0].FileName = fileName;
                    shaMatches[0].LastModifiedUtc = File.GetLastWriteTimeUtc(sourcePath);
                    await _libraryManager.SaveManifestAsync(manifest);
                    continue;
                }
            }

            if (!rebuildIndex && current is not null && string.Equals(current.Sha256, sha, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var storedFileName = $"{sha[..12]}_{fileName}";
            var storedRelativePath = Path.Combine("files", storedFileName).Replace('\\', '/');
            var storedAbsPath = Path.Combine(_libraryManager.GetLibraryPath(manifest.Id), storedRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(storedAbsPath)!);
            File.Copy(sourcePath, storedAbsPath, overwrite: true);

            // --- Security: Magic-byte / extension validation ---
            ParsedDocument parsed;
            try
            {
                parsed = DocumentParser.Parse(storedAbsPath);
            }
            catch (InvalidOperationException ex)
            {
                _logger?.Warn($"Rejected file failing content validation: {sourcePath} — {ex.Message}");
                // Clean up the stored copy that was already written.
                if (File.Exists(storedAbsPath))
                {
                    File.Delete(storedAbsPath);
                }
                continue;
            }

            try
            {
                // Flatten all text chunks with their segment metadata, preserving document order.
                var textItems = new List<(string Text, int? Page)>();
                foreach (var segment in parsed.Segments)
                {
                    var texts = DocumentChunker.ChunkText(segment.Text, config.ChunkSize, config.ChunkOverlap);
                    foreach (var text in texts)
                        textItems.Add((text, segment.Page));
                }

                var totalChunks = textItems.Count;
                if (totalChunks == 0)
                {
                    var error = $"Ingestion failed for '{fileName}': no chunks were generated after parsing and chunking.";
                    _logger?.Error(error);
                    throw new InvalidOperationException(error);
                }

                var embeddedChunks = 0;
                var failedChunkCount = 0;
                var results = new DocumentChunk?[totalChunks];

                var maxConcurrency = Math.Max(1, config.MaxEmbeddingConcurrency);
                using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

                var tasks = textItems.Select(async (item, i) =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        var embedding = await _embeddingClient.EmbedAsync(host, config.EmbeddingModelName, item.Text, cancellationToken);
                        results[i] = new DocumentChunk
                        {
                            LibraryId = manifest.Id,
                            SourceFileName = fileName,
                            StoredRelativePath = storedRelativePath,
                            Page = item.Page,
                            ChunkIndex = i,
                            Text = item.Text,
                            TextLength = item.Text.Length,
                            Sha256 = sha,
                            Embedding = embedding
                        };
                        var completed = Interlocked.Increment(ref embeddedChunks);
                        progress?.Invoke(new IndexingProgress
                        {
                            TotalFiles = total,
                            CompletedFiles = done - 1,
                            CurrentFile = fileName,
                            EmbeddedChunks = completed,
                            TotalChunks = totalChunks
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref failedChunkCount);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks);

                // Collect successfully embedded chunks in original document order.
                var chunks = results.Where(r => r is not null).Select(r => r!).ToList();
                var failureRatio = totalChunks == 0 ? 0d : (double)failedChunkCount / totalChunks;

                if (failedChunkCount > 0)
                {
                    progress?.Invoke(new IndexingProgress
                    {
                        TotalFiles = total,
                        CompletedFiles = done - 1,
                        CurrentFile = fileName,
                        EmbeddedChunks = embeddedChunks,
                        TotalChunks = totalChunks,
                        FailedChunks = failedChunkCount
                    });
                }

                if (failureRatio > MaxEmbeddingFailureRatioBeforeAbort)
                {
                    var error =
                        $"Ingestion failed for '{fileName}': embedding failures exceeded threshold " +
                        $"(total={totalChunks}, succeeded={embeddedChunks}, failed={failedChunkCount}, ratio={failureRatio:P1}, threshold={MaxEmbeddingFailureRatioBeforeAbort:P0}).";
                    _logger?.Error(error);
                    throw new InvalidOperationException(error);
                }

                // Delete-on-replace: when sha changed the storedRelativePath changes too.
                // Purge old chunks and stored copy before inserting the new ones.
                // Crash window: if the process dies here the manifest still references the old
                // StoredRelativePath; the next ingest will re-trigger cleanup.
                if (current is not null && !string.Equals(current.StoredRelativePath, storedRelativePath, StringComparison.OrdinalIgnoreCase))
                {
                    var oldStoredAbsPath = Path.Combine(_libraryManager.GetLibraryPath(manifest.Id), current.StoredRelativePath.Replace('/', Path.DirectorySeparatorChar));
                    vectorIndex.RemoveFile(manifest.Id, current.StoredRelativePath);
                    TryDeleteStoredFile(oldStoredAbsPath);
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

                // Persist the manifest incrementally so that vectors committed per-file
                // (UpsertFileChunks above) do not become orphaned if a later file in the
                // batch fails or the operation is cancelled before the final save below.
                await _libraryManager.SaveManifestAsync(manifest);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Per-file failure: roll back the staged copy on disk (the vectors
                // were only committed on the success path above) and record the
                // error so the remaining files in the batch can still be processed.
                // Aggregated errors are thrown after the loop so callers still see
                // the failure.
                TryDeleteStoredFile(storedAbsPath);
                perFileErrors.Add(ex);
            }
        }

        manifest.LastIndexedUtc = DateTime.UtcNow;
        await _libraryManager.SaveManifestAsync(manifest);
        progress?.Invoke(new IndexingProgress { TotalFiles = total, CompletedFiles = total, CurrentFile = string.Empty });

        if (perFileErrors.Count == 1)
        {
            // Preserve the single-file-failure contract: callers (and tests) that
            // expect the original InvalidOperationException continue to see it.
            throw perFileErrors[0];
        }
        if (perFileErrors.Count > 1)
        {
            throw new AggregateException(
                $"Document ingestion completed with {perFileErrors.Count} file failure(s); successful files were persisted.",
                perFileErrors);
        }
    }

    private static void TryDeleteStoredFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; leaving a stray file on disk is preferable to
            // masking the underlying ingestion failure with an IO exception.
        }
    }

    public async Task SweepFoldersAsync(DocumentLibraryManifest manifest, string host, PortableConfig config, Action<IndexingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var files = new List<string>();
        foreach (var folder in manifest.WatchedFolders.Where(Directory.Exists))
        {
            var allowedRoot = Path.GetFullPath(folder);
            foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
            {
                if (!DocumentParser.IsSupported(file))
                    continue;

                // --- Security: Path traversal guard for watched folders ---
                try
                {
                    PathGuards.EnsureUnderRoot(allowedRoot, file);
                }
                catch (InvalidOperationException ex)
                {
                    _logger?.Warn($"Path traversal blocked in watched folder '{folder}': {file} — {ex.Message}");
                    continue;
                }

                files.Add(file);
            }
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
