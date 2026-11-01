using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SupportDesk.Core.AI;
using SupportDesk.Core.Retrieval;

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

    [Fact]
    public async Task The_callers_cancellation_stays_a_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var service = Service(new ThrowingGenerator(new OperationCanceledException(cts.Token)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchAsync("refund", 5, cts.Token));
    }

    private static RetrievalService Service(IEmbeddingGenerator<string, Embedding<float>> generator) =>
        new(new EmptyStore(), generator, Options.Create(new AiOptions()), Options.Create(new RetrievalOptions()));

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

    private sealed class EmptyStore : IArticleChunkStore
    {
        public Task<IReadOnlyList<StoredChunk>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<StoredChunk>>([]);

        public Task SaveAsync(ChunkChanges changes, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(ReadOnlyMemory<float> vector, string embeddingModel, int k, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RetrievedChunk>>([]);
    }
}
