namespace FreeAiSsd.Runner.Services;

/// <summary>
/// Tracks whether a long-running document indexing operation (ingest, sweep,
/// or rebuild) is currently in flight on the host. The chat path reads this so
/// it can warn callers that RAG retrieval may be incomplete, and clients can
/// render a "documents indexing / not ready" state instead of silently
/// querying a half-built index (task #99).
///
/// Thread-safe: indexing fans embed work out across worker threads and the API
/// host can run more than one indexing request at a time, so begin/end is an
/// interlocked counter rather than a single bool.
/// </summary>
public sealed class IndexingActivity
{
    private int _active;

    /// <summary>True while at least one indexing operation is running.</summary>
    public bool InProgress => Volatile.Read(ref _active) > 0;

    /// <summary>Number of indexing operations currently in flight.</summary>
    public int ActiveCount => Volatile.Read(ref _active);

    /// <summary>
    /// Marks an indexing operation as started. Dispose the returned scope when
    /// the operation finishes (success or failure). Disposing is idempotent so
    /// a double-dispose can't drive the counter negative.
    /// </summary>
    public IDisposable Begin()
    {
        Interlocked.Increment(ref _active);
        return new Scope(this);
    }

    private sealed class Scope : IDisposable
    {
        private IndexingActivity? _owner;

        public Scope(IndexingActivity owner) => _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null)
            {
                Interlocked.Decrement(ref owner._active);
            }
        }
    }
}
