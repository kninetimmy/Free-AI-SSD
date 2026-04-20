namespace FreeAiSsd.Shared.Documents;

public sealed class EmbeddingModelMismatchException : Exception
{
    public string LibraryId { get; }
    public int StoredDimension { get; }
    public int RequestedDimension { get; }

    public EmbeddingModelMismatchException(string libraryId, int storedDimension, int requestedDimension)
        : base($"Embedding dimension mismatch in library '{libraryId}': stored={storedDimension}, current={requestedDimension}. Reindex required.")
    {
        LibraryId = libraryId;
        StoredDimension = storedDimension;
        RequestedDimension = requestedDimension;
    }
}
