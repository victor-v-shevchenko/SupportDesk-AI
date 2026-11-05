using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SupportDesk.Core.AI;
using SupportDesk.Core.Telemetry;

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
    IOptions<RetrievalOptions> retrievalOptions,
    TimeProvider time) : IRetrievalService
{
    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int k, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        // The query is ticket text for a reply: its length only
        using var activity = SupportDeskTelemetry.Source.StartActivity("retrieval");
        activity?.SetTag("supportdesk.query_length", query.Length);
        activity?.SetTag("supportdesk.k", k);
        activity?.SetTag("supportdesk.min_similarity", retrievalOptions.Value.MinSimilarity);
        try
        {
            var kept = await SearchCoreAsync(query, k, cancellationToken);
            activity?.SetTag("supportdesk.results_kept", kept.Count);
            return kept;
        }
        catch (Exception exception) when (activity.RecordedFailure(exception, cancellationToken))
        {
            throw;
        }
    }

    private async Task<IReadOnlyList<RetrievedChunk>> SearchCoreAsync(string query, int k, CancellationToken cancellationToken)
    {
        var embeddings = aiOptions.Value.Embeddings;
        GeneratedEmbeddings<Embedding<float>> generated;
        try
        {
            generated = await EmbeddingTimeout.RunAsync(
                time, aiOptions.Value.Timeouts.EmbeddingSeconds, token => generator.GenerateAsync([query], cancellationToken: token), cancellationToken);
        }
        catch (Exception exception) when (exception is not ModelUnavailableException)
        {
            // Whatever failed after the caller cancelled (even a timeout that fired just before) is the caller's cancellation
            cancellationToken.ThrowIfCancellationRequested();
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
    /// a cancellation the caller didn't ask for, or the limit of <c>AI:Timeouts:EmbeddingSeconds</c>, is a timeout, an HTTP error keeps its status. The message is fixed text,
    /// since the provider's can echo the query, which is ticket text for a reply.
    /// </summary>
    private static ModelUnavailableException ToModelUnavailable(Exception exception)
    {
        var http = exception as HttpRequestException;
        var status = (int?)http?.StatusCode;
        var timeout = exception is OperationCanceledException or TimeoutException;
        // The retry rules of PLAN.md 6.5: a connection error or a transient status, not a 401 or 404
        var httpTransient = http is not null && (status is not { } code || ModelError.FromStatus(code).IsTransient);
        return new ModelUnavailableException(
            timeout ? "The embedding provider timed out." : "The embedding provider failed.",
            exception,
            status,
            isTransient: timeout || httpTransient)
        {
            IsTimeout = timeout,
        };
    }
}
