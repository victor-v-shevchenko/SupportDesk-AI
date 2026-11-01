namespace SupportDesk.Core.Retrieval;

/// <summary>Stored article chunks and their vectors (the <c>ArticleChunks</c> table).</summary>
public interface IArticleChunkStore
{
    /// <summary>Every stored chunk, without its vector.</summary>
    Task<IReadOnlyList<StoredChunk>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Applies all the changes in one transaction.</summary>
    Task SaveAsync(ChunkChanges changes, CancellationToken cancellationToken);

    /// <summary>
    /// The <paramref name="k"/> chunks embedded with <paramref name="embeddingModel"/> nearest to <paramref name="vector"/> by
    /// cosine distance, most similar first.
    /// </summary>
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(ReadOnlyMemory<float> vector, string embeddingModel, int k, CancellationToken cancellationToken);
}

public sealed record StoredChunk(long Id, int ArticleId, int Ordinal, string ContentHash, string EmbeddingModel);

/// <summary>What a sync changes: chunks to add (with their vectors), kept chunks whose position moved, and chunks to delete.</summary>
public sealed record ChunkChanges(IReadOnlyList<NewChunk> Added, IReadOnlyList<MovedChunk> Moved, IReadOnlyList<long> Deleted)
{
    public bool IsEmpty => Added.Count == 0 && Moved.Count == 0 && Deleted.Count == 0;
}

public sealed record NewChunk(int ArticleId, ArticleChunkDraft Chunk, string EmbeddingModel, ReadOnlyMemory<float> Embedding);

public sealed record MovedChunk(long Id, int Ordinal);

/// <summary>A search result. <see cref="Similarity"/> is cosine similarity, 1 minus the cosine distance.</summary>
public sealed record RetrievedChunk(string ArticleSlug, string ArticleTitle, string? Heading, string Text, double Similarity);
