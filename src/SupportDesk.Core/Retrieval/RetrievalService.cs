using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SupportDesk.Core.AI;

namespace SupportDesk.Core.Retrieval;

public interface IRetrievalService
{
    /// <summary>The up to <paramref name="k"/> chunks most similar to <paramref name="query"/>, most similar first, without those below the minimum similarity.</summary>
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int k, CancellationToken cancellationToken);
}

public sealed class RetrievalService(
    IArticleChunkStore store,
    IEmbeddingGenerator<string, Embedding<float>> generator,
    IOptions<AiOptions> aiOptions,
    IOptions<RetrievalOptions> retrievalOptions) : IRetrievalService
{
    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int k, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        var embeddings = aiOptions.Value.Embeddings;
        var vectors = await CheckedEmbeddings.GenerateAsync(generator, [query], embeddings.ResolvedModel, embeddings.Dimensions, cancellationToken);

        // The store returns the nearest k (so the HNSW index can serve the query); the threshold is applied after
        var nearest = await store.SearchAsync(vectors[0], embeddings.ResolvedModel, k, cancellationToken);
        var minimum = retrievalOptions.Value.MinSimilarity;
        return [.. nearest.Where(c => c.Similarity >= minimum)];
    }
}
