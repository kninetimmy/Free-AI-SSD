using Microsoft.Data.Sqlite;

namespace FreeAiSsd.Shared.Documents;

public sealed class DocumentIngestor
{
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
        // Materialize the distinct request set first so unsupported-extension files
        // (dropped before the loop) can be counted toward the terminal skip total.
        var requested = sourcePaths.Distinct().ToList();
        var candidates = requested.Where(DocumentParser.IsSupported).ToList();
        var total = candidates.Count;
        var done = 0;
        var maxSizeBytes = (long)config.MaxDocumentSizeMB * 1024 * 1024;
        var perFileErrors = new List<Exception>();
        // Terminal-frame summary counters: unsupported-extension files are skipped before
        // the loop; in-loop skips (non-existent, symlink, oversize, content-validation) are
        // tallied below. Batch chunk totals accumulate across successfully embedded files.
        var unsupportedSkipped = requested.Count - total;
        var inLoopSkipped = 0;
        var batchEmbedded = 0;
        var batchTotalChunks = 0;
        var batchFailedChunks = 0;
        var failureThreshold = EffectiveFailureThreshold(config);

        foreach (var sourcePath in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            done++;
            progress?.Invoke(new IndexingProgress { TotalFiles = total, CompletedFiles = done - 1, CurrentFile = Path.GetFileName(sourcePath) });

            if (!File.Exists(sourcePath))
            {
                inLoopSkipped++;
                continue;
            }

            // --- Security: Symlink / reparse-point detection ---
            var fileAttributes = File.GetAttributes(sourcePath);
            if ((fileAttributes & FileAttributes.ReparsePoint) != 0)
            {
                _logger?.Warn($"Rejected symlink/reparse point: {sourcePath}");
                inLoopSkipped++;
                continue;
            }

            // --- Security: File size limit ---
            var fileSize = new FileInfo(sourcePath).Length;
            if (fileSize > maxSizeBytes)
            {
                _logger?.Warn($"Rejected oversized file ({fileSize / (1024.0 * 1024.0):F1} MB exceeds {config.MaxDocumentSizeMB} MB limit): {sourcePath}");
                inLoopSkipped++;
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
                    var renamed = shaMatches[0];
                    renamed.SourceOriginalPath = sourcePath;
                    renamed.FileName = fileName;
                    renamed.LastModifiedUtc = File.GetLastWriteTimeUtc(sourcePath);
                    vectorIndex.UpdateFileName(manifest.Id, renamed.StoredRelativePath, fileName);
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
                inLoopSkipped++;
                continue;
            }

            try
            {
                // Section-aware chunking, preserving document order.
                var spans = BuildChunkSpans(parsed, config);

                var totalChunks = spans.Count;
                if (totalChunks == 0)
                {
                    var error = $"Ingestion failed for '{fileName}': no chunks were generated after parsing and chunking.";
                    _logger?.Error(error);
                    throw new InvalidOperationException(error);
                }

                var embeddedChunks = 0;
                var failedChunkCount = 0;
                var results = new DocumentChunk?[totalChunks];
                // C2: capture the first per-chunk exception so the
                // threshold abort message can name the actual cause
                // (e.g. "model 'nomic-embed-text' not found"). Without
                // this the user sees only "ratio=100% threshold=50%"
                // and has no path to the underlying provisioning gap.
                Exception? firstFailure = null;

                await EmbedSpansBatchedAsync(
                    host, config, spans, results,
                    buildChunk: (chunkIndex, span, embedding) => new DocumentChunk
                    {
                        LibraryId = manifest.Id,
                        SourceFileName = fileName,
                        StoredRelativePath = storedRelativePath,
                        Page = span.Page,
                        ChunkIndex = chunkIndex,
                        Text = span.Text,
                        TextLength = span.Text.Length,
                        Sha256 = sha,
                        Embedding = embedding,
                        EmbeddingModel = config.EmbeddingModelName,
                        EmbeddingDimension = embedding.Length,
                        ParserVersion = DocumentParser.Version,
                        ChunkerVersion = DocumentChunker.Version,
                        Section = span.Section,
                        HeadingPath = span.HeadingPath,
                        CharOffsetStart = span.CharOffsetStart,
                        CharOffsetEnd = span.CharOffsetEnd,
                        ContentType = span.ContentType,
                    },
                    onChunkEmbedded: () =>
                    {
                        var completed = Interlocked.Increment(ref embeddedChunks);
                        progress?.Invoke(new IndexingProgress
                        {
                            TotalFiles = total,
                            CompletedFiles = done - 1,
                            CurrentFile = fileName,
                            EmbeddedChunks = completed,
                            TotalChunks = totalChunks
                        });
                    },
                    onChunkFailed: ex =>
                    {
                        Interlocked.CompareExchange(ref firstFailure, ex, null);
                        Interlocked.Increment(ref failedChunkCount);
                    },
                    cancellationToken);

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

                if (failureRatio > failureThreshold)
                {
                    var causeSuffix = firstFailure is null
                        ? string.Empty
                        : $" First failure: {firstFailure.Message}";
                    var error =
                        $"Ingestion failed for '{fileName}': embedding failures exceeded threshold " +
                        $"(total={totalChunks}, succeeded={embeddedChunks}, failed={failedChunkCount}, ratio={failureRatio:P1}, threshold={failureThreshold:P0}).{causeSuffix}";
                    _logger?.Error(error);
                    throw new InvalidOperationException(error);
                }

                // A loosened threshold (e.g. 1.0) can clear the check above with zero
                // surviving chunks; fail loudly with the cause rather than letting the
                // chunks[0] provenance read below throw an opaque IndexOutOfRange.
                if (chunks.Count == 0)
                {
                    var causeSuffix = firstFailure is null
                        ? string.Empty
                        : $" First failure: {firstFailure.Message}";
                    var error =
                        $"Ingestion failed for '{fileName}': no chunks embedded successfully " +
                        $"(total={totalChunks}, failed={failedChunkCount}).{causeSuffix}";
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

                vectorIndex.CheckProvenance(manifest.Id, config.EmbeddingModelName, chunks[0].EmbeddingDimension, _logger);
                vectorIndex.UpsertFileChunks(manifest.Id, storedRelativePath, chunks);

                manifest.LastEmbeddingModel = config.EmbeddingModelName;
                manifest.LastEmbeddingDimension = chunks[0].EmbeddingDimension;

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

                batchEmbedded += embeddedChunks;
                batchTotalChunks += totalChunks;
                batchFailedChunks += failedChunkCount;
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
        // Terminal frame (CurrentFile empty): CompletedFiles is the indexed/updated count
        // (in-loop skips excluded), SkippedFiles is every skipped file, and the chunk fields
        // carry the batch totals so the UI can render an honest completion summary.
        // FailedChunks is the authoritative batch total of tolerated dropped chunks; the
        // summary renderers read it here rather than summing per-file frames.
        progress?.Invoke(new IndexingProgress
        {
            TotalFiles = total,
            CompletedFiles = total - inLoopSkipped,
            CurrentFile = string.Empty,
            EmbeddedChunks = batchEmbedded,
            TotalChunks = batchTotalChunks,
            FailedChunks = batchFailedChunks,
            SkippedFiles = unsupportedSkipped + inLoopSkipped
        });

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

    /// <summary>
    /// Single chokepoint turning a parsed document into ordered, section-aware chunk spans.
    /// Both the fresh-ingest and rebuild-from-stored-copy paths go through here so they stay
    /// in lockstep on chunking behavior.
    /// </summary>
    private static List<DocumentChunkSpan> BuildChunkSpans(ParsedDocument parsed, PortableConfig config)
        => DocumentChunker.ChunkBlocks(parsed.Blocks, config.ChunkSize, config.ChunkOverlap);

    /// <summary>
    /// Embeds <paramref name="spans"/> into <paramref name="results"/> in batches of
    /// <c>config.EmbeddingBatchSize</c>, with up to <c>config.MaxEmbeddingConcurrency</c>
    /// batches in flight. Each embedded chunk fires <paramref name="onChunkEmbedded"/>;
    /// each failure fires <paramref name="onChunkFailed"/>. A whole-batch HTTP failure
    /// falls back to per-chunk embedding (<see cref="EmbedBatchPerChunkAsync"/>) so one
    /// bad chunk can't drop the rest of the batch and skew the failure ratio (X18). Each
    /// chunk is written at its original index, so document order is preserved regardless
    /// of completion order. Shared by the fresh-ingest and rebuild-from-stored paths so
    /// they stay in lockstep on embedding behavior.
    /// </summary>
    private async Task EmbedSpansBatchedAsync(
        string host,
        PortableConfig config,
        IReadOnlyList<DocumentChunkSpan> spans,
        DocumentChunk?[] results,
        Func<int, DocumentChunkSpan, float[], DocumentChunk> buildChunk,
        Action onChunkEmbedded,
        Action<Exception> onChunkFailed,
        CancellationToken cancellationToken)
    {
        var batchSize = Math.Max(1, config.EmbeddingBatchSize);
        var maxConcurrency = Math.Max(1, config.MaxEmbeddingConcurrency);
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var batches = new List<(int Start, int Count)>();
        for (var start = 0; start < spans.Count; start += batchSize)
        {
            batches.Add((start, Math.Min(batchSize, spans.Count - start)));
        }

        var tasks = batches.Select(async batch =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var inputs = new string[batch.Count];
                for (var k = 0; k < batch.Count; k++)
                {
                    inputs[k] = spans[batch.Start + k].Text;
                }

                float[][] embeddings;
                try
                {
                    embeddings = await _embeddingClient.EmbedBatchAsync(
                        host, config.EmbeddingModelName, inputs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Isolate the failure: retry this batch one chunk at a time so
                    // a single bad chunk doesn't fail the whole batch.
                    await EmbedBatchPerChunkAsync(
                        host, config, spans, batch.Start, batch.Count, results,
                        buildChunk, onChunkEmbedded, onChunkFailed, cancellationToken);
                    return;
                }

                for (var k = 0; k < batch.Count; k++)
                {
                    var chunkIndex = batch.Start + k;
                    results[chunkIndex] = buildChunk(chunkIndex, spans[chunkIndex], embeddings[k]);
                    onChunkEmbedded();
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Per-chunk fallback for a failed batch embed: embeds each chunk in the
    /// failed range individually so transient or single-chunk failures stay
    /// isolated to that chunk and the rest still land.
    /// </summary>
    private async Task EmbedBatchPerChunkAsync(
        string host,
        PortableConfig config,
        IReadOnlyList<DocumentChunkSpan> spans,
        int start,
        int count,
        DocumentChunk?[] results,
        Func<int, DocumentChunkSpan, float[], DocumentChunk> buildChunk,
        Action onChunkEmbedded,
        Action<Exception> onChunkFailed,
        CancellationToken cancellationToken)
    {
        for (var k = 0; k < count; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkIndex = start + k;
            try
            {
                var embedding = await _embeddingClient.EmbedAsync(
                    host, config.EmbeddingModelName, spans[chunkIndex].Text, cancellationToken);
                results[chunkIndex] = buildChunk(chunkIndex, spans[chunkIndex], embedding);
                onChunkEmbedded();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                onChunkFailed(ex);
            }
        }
    }

    /// <summary>
    /// The configured per-file embedding-failure abort threshold, clamped to [0, 1] so an
    /// out-of-range config value can't invert the comparison or starve every ingest.
    /// </summary>
    private static double EffectiveFailureThreshold(PortableConfig config)
        => Math.Clamp(config.MaxEmbeddingFailureRatioBeforeAbort, 0d, 1d);

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
            // Flush pooled SQLite connections before deletion; an open pool entry
            // holds the OS file lock and causes "file is being used by another process".
            // Safe because vectors.db is the only SQLite DB in this process.
            SqliteConnection.ClearAllPools();
            File.Delete(db);
        }

        var libraryPath = _libraryManager.GetLibraryPath(manifest.Id);
        var sourceFiles = new List<string>();
        var storedFallbacks = new List<(DocumentFileEntry Entry, string StoredAbsPath)>();
        var toRemove = new List<DocumentFileEntry>();

        foreach (var entry in manifest.Files)
        {
            var storedAbsPath = Path.Combine(libraryPath, entry.StoredRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(entry.SourceOriginalPath))
                sourceFiles.Add(entry.SourceOriginalPath);
            else if (File.Exists(storedAbsPath))
                storedFallbacks.Add((entry, storedAbsPath));
            else
            {
                toRemove.Add(entry);
                _logger?.Warn($"Dropping '{entry.FileName}' from manifest: source and stored copy both missing.");
            }
        }

        foreach (var entry in toRemove)
            manifest.Files.Remove(entry);
        if (toRemove.Count > 0)
            await _libraryManager.SaveManifestAsync(manifest);

        if (storedFallbacks.Count > 0)
        {
            var vectorIndex = new VectorIndex(indexPath);
            var perFileErrors = new List<Exception>();
            foreach (var (entry, storedAbsPath) in storedFallbacks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await EmbedStoredCopyAsync(manifest, entry, storedAbsPath, host, config, vectorIndex, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    perFileErrors.Add(ex);
                }
            }
            if (perFileErrors.Count == 1) throw perFileErrors[0];
            if (perFileErrors.Count > 1)
                throw new AggregateException(
                    $"Rebuild from stored copies completed with {perFileErrors.Count} failure(s); successful files were re-embedded.",
                    perFileErrors);
        }

        await IngestFilesAsync(manifest, sourceFiles, host, config, progress, rebuildIndex: true, cancellationToken: cancellationToken);
    }

    private async Task EmbedStoredCopyAsync(
        DocumentLibraryManifest manifest,
        DocumentFileEntry entry,
        string parsePath,
        string host,
        PortableConfig config,
        VectorIndex vectorIndex,
        CancellationToken cancellationToken)
    {
        var parsed = DocumentParser.Parse(parsePath);
        var spans = BuildChunkSpans(parsed, config);

        if (spans.Count == 0)
        {
            _logger?.Warn($"Stored copy for '{entry.FileName}' yielded no chunks during rebuild — skipping.");
            return;
        }

        var results = new DocumentChunk?[spans.Count];
        var failedCount = 0;
        // C2: same first-failure capture as IngestFilesAsync — see the
        // comment there for rationale.
        Exception? firstFailure = null;

        await EmbedSpansBatchedAsync(
            host, config, spans, results,
            buildChunk: (chunkIndex, span, embedding) => new DocumentChunk
            {
                LibraryId = manifest.Id,
                SourceFileName = entry.FileName,
                StoredRelativePath = entry.StoredRelativePath,
                Page = span.Page,
                ChunkIndex = chunkIndex,
                Text = span.Text,
                TextLength = span.Text.Length,
                Sha256 = entry.Sha256,
                Embedding = embedding,
                EmbeddingModel = config.EmbeddingModelName,
                EmbeddingDimension = embedding.Length,
                ParserVersion = DocumentParser.Version,
                ChunkerVersion = DocumentChunker.Version,
                Section = span.Section,
                HeadingPath = span.HeadingPath,
                CharOffsetStart = span.CharOffsetStart,
                CharOffsetEnd = span.CharOffsetEnd,
                ContentType = span.ContentType,
            },
            onChunkEmbedded: () => { },
            onChunkFailed: ex =>
            {
                Interlocked.CompareExchange(ref firstFailure, ex, null);
                Interlocked.Increment(ref failedCount);
            },
            cancellationToken);

        var totalChunks = spans.Count;
        var failureRatio = totalChunks == 0 ? 0d : (double)failedCount / totalChunks;
        var failureThreshold = EffectiveFailureThreshold(config);
        if (failureRatio > failureThreshold)
        {
            var causeSuffix = firstFailure is null
                ? string.Empty
                : $" First failure: {firstFailure.Message}";
            var error =
                $"Rebuild from stored copy failed for '{entry.FileName}': embedding failures exceeded threshold " +
                $"(total={totalChunks}, failed={failedCount}, ratio={failureRatio:P1}, threshold={failureThreshold:P0}).{causeSuffix}";
            _logger?.Error(error);
            throw new InvalidOperationException(error);
        }

        var chunks = results.Where(r => r is not null).Select(r => r!).ToList();
        if (chunks.Count == 0)
        {
            // See IngestFilesAsync: a loosened threshold can survive the ratio check with
            // nothing embedded; fail with the cause instead of an opaque chunks[0] throw.
            var causeSuffix = firstFailure is null
                ? string.Empty
                : $" First failure: {firstFailure.Message}";
            var error =
                $"Rebuild from stored copy failed for '{entry.FileName}': no chunks embedded successfully " +
                $"(total={totalChunks}, failed={failedCount}).{causeSuffix}";
            _logger?.Error(error);
            throw new InvalidOperationException(error);
        }

        vectorIndex.CheckProvenance(manifest.Id, config.EmbeddingModelName, chunks[0].EmbeddingDimension, _logger);
        vectorIndex.UpsertFileChunks(manifest.Id, entry.StoredRelativePath, chunks);
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
