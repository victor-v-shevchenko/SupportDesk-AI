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
        GeneratedEmbeddings<Embedding<float>> generated;
        try
        {
            generated = await generator.GenerateAsync([query], cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not ModelUnavailableException && !cancellationToken.IsCancellationRequested)
        {
            throw ToModelUnavailable(exception);
        }

        // Outside the try: a wrong vector count or size is a configuration error with a useful message, not an outage
        var vectors = CheckedEmbeddings.Check(generated, 1, embeddings.ResolvedModel, embeddings.Dimensions);

        // The store returns the nearest k (so the HNSW index can serve the query); the threshold is applied after
        var nearest = await store.SearchAsync(vectors[0], embeddings.ResolvedModel, k, cancellationToken);
        var minimum = retrievalOptions.Value.MinSimilarity;
        return [.. nearest.Where(c => c.Similarity >= minimum)];
    }

    /// <summary>
    /// Embedding providers aren't behind the chat pipeline's error mapping (PLAN.md 6.5), so their failures are mapped here:
    /// a cancellation the caller didn't ask for is a timeout, an HTTP error keeps its status. The message is fixed text,
    /// since the provider's can echo the query, which is ticket text for a reply.
    /// </summary>
    private static ModelUnavailableException ToModelUnavailable(Exception exception)
    {
        var status = (int?)(exception as HttpRequestException)?.StatusCode;
        var timeout = exception is OperationCanceledException;
        return new ModelUnavailableException(
            timeout ? "The embedding provider timed out." : "The embedding provider failed.",
            exception,
            status,
            isTransient: timeout || exception is HttpRequestException)
        {
            IsTimeout = timeout,
        };
    }
}
