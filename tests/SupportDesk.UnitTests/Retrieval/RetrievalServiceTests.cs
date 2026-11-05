using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SupportDesk.Core.AI;
using SupportDesk.Core.Replies;
using SupportDesk.Core.Retrieval;
using SupportDesk.Core.Telemetry;
using SupportDesk.UnitTests.Telemetry;

namespace SupportDesk.UnitTests.Retrieval;

public class RetrievalServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<Exception> ProviderFailures => new()
    {
        new HttpRequestException("Connection refused", null, null),
        new HttpRequestException("Bad gateway", null, HttpStatusCode.BadGateway),
        new TaskCanceledException("HttpClient timeout"),
        new InvalidOperationException("SDK failure"),
    };

    [Theory]
    [MemberData(nameof(ProviderFailures))]
    public async Task An_embedding_provider_failure_becomes_ModelUnavailableException(Exception failure)
    {
        var service = Service(new ThrowingGenerator(failure));

        var exception = await Assert.ThrowsAsync<ModelUnavailableException>(() => service.SearchAsync("refund", 5, Ct));

        Assert.Same(failure, exception.InnerException);
        Assert.Equal((int?)(failure as HttpRequestException)?.StatusCode, exception.StatusCode);
        Assert.Equal(failure is TaskCanceledException, exception.IsTimeout);
        // The message never carries the provider's, which can echo the query (ticket text)
        Assert.DoesNotContain(failure.Message, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    public async Task An_embedding_http_error_is_transient_by_the_retry_rules_of_its_status(HttpStatusCode status, bool transient)
    {
        var service = Service(new ThrowingGenerator(new HttpRequestException("failed", null, status)));

        var exception = await Assert.ThrowsAsync<ModelUnavailableException>(() => service.SearchAsync("refund", 5, Ct));

        Assert.Equal(transient, exception.IsTransient);
    }

    [Fact]
    public async Task An_embedding_connection_error_is_transient()
    {
        var service = Service(new ThrowingGenerator(new HttpRequestException("Connection refused", null, null)));

        var exception = await Assert.ThrowsAsync<ModelUnavailableException>(() => service.SearchAsync("refund", 5, Ct));

        Assert.True(exception.IsTransient);
    }

    [Fact]
    public async Task A_vector_of_the_wrong_size_stays_a_configuration_error()
    {
        var service = Service(new FixedGenerator(new float[3]));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync("refund", 5, Ct));

        Assert.Contains("returned 3-dimension vectors, but AI:Embeddings:Dimensions is 1024", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_callers_cancellation_stays_a_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var service = Service(new ThrowingGenerator(new OperationCanceledException(cts.Token)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchAsync("refund", 5, cts.Token));
    }

    [Fact]
    public async Task A_search_has_a_span_with_the_query_length_k_and_results_but_not_the_query()
    {
        using var trace = new TraceCapture();
        const string Query = "QUERYMARKER-90ab where is my refund";

        await Service(new FixedGenerator(new float[1024])).SearchAsync(Query, 5, Ct);

        var span = Assert.Single(trace.Named("retrieval"));
        Assert.Equal(Query.Length, span.GetTagItem("supportdesk.query_length"));
        Assert.Equal(5, span.GetTagItem("supportdesk.k"));
        Assert.Equal(0, span.GetTagItem("supportdesk.results_kept"));
        Assert.Equal(new RetrievalOptions().MinSimilarity, span.GetTagItem("supportdesk.min_similarity"));
        Assert.DoesNotContain(trace.Spans.SelectMany(TraceCapture.AllText), text => text.Contains("QUERYMARKER", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_search_is_an_error_span_with_the_code_and_not_the_providers_message()
    {
        using var trace = new TraceCapture();
        var service = Service(new ThrowingGenerator(new HttpRequestException("echo of QUERYMARKER-90ab", null, HttpStatusCode.BadGateway)));

        await Assert.ThrowsAsync<ModelUnavailableException>(() => service.SearchAsync("refund", 5, Ct));

        var span = Assert.Single(trace.Named("retrieval"));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(ReplyErrorCodes.ModelUnavailable, span.StatusDescription);
        Assert.DoesNotContain(trace.Spans.SelectMany(TraceCapture.AllText), text => text.Contains("QUERYMARKER", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_search_the_caller_cancelled_is_not_an_error_span()
    {
        using var trace = new TraceCapture();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var service = Service(new ThrowingGenerator(new OperationCanceledException(cts.Token)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchAsync("refund", 5, cts.Token));

        var span = Assert.Single(trace.Named("retrieval"));
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.Equal(true, span.GetTagItem(SupportDeskTelemetry.Cancelled));
    }

    private static RetrievalService Service(IEmbeddingGenerator<string, Embedding<float>> generator) =>
        new(new EmptyStore(), generator, Options.Create(new AiOptions()), Options.Create(new RetrievalOptions()), TimeProvider.System);

    private sealed class ThrowingGenerator(Exception failure) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromException<GeneratedEmbeddings<Embedding<float>>>(failure);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
            // Nothing to release
        }
    }

    private sealed class FixedGenerator(float[] vector) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(_ => new Embedding<float>(vector))));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
            // Nothing to release
        }
    }

    private sealed class EmptyStore : IArticleChunkStore
    {
        public Task<IReadOnlyList<StoredChunk>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<StoredChunk>>([]);

        public Task SaveAsync(ChunkChanges changes, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(ReadOnlyMemory<float> vector, string embeddingModel, int k, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RetrievedChunk>>([]);
    }
}
