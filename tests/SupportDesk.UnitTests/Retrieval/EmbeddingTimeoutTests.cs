using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SupportDesk.Core.AI;
using SupportDesk.Core.Replies;
using SupportDesk.Core.Retrieval;
using SupportDesk.UnitTests.Adversarial;
using SupportDesk.UnitTests.AI;
using static SupportDesk.UnitTests.Adversarial.ReplyRig;

namespace SupportDesk.UnitTests.Retrieval;

/// <summary>PLAN.md 6.5: an embedding call that is accepted and never answered ends at <c>AI:Timeouts:EmbeddingSeconds</c>, on the injected clock.</summary>
[Collection(FakeClockGroup.Name)]
public sealed class EmbeddingTimeoutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_search_whose_embedding_never_returns_ends_as_a_timeout_after_the_setting()
    {
        var time = new FakeTimeProvider();
        var generator = new StalledGenerator();
        var service = Service(generator, time);

        var search = service.SearchAsync("refund", 5, Ct);
        await generator.Started.WaitAsync(Ct);
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(search.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<ModelUnavailableException>(() => search);
        Assert.True(exception.IsTimeout);
        Assert.True(exception.IsTransient);
    }

    [Fact]
    public async Task The_callers_cancellation_during_a_stalled_embedding_stays_a_cancellation()
    {
        var generator = new StalledGenerator();
        var service = Service(generator, new FakeTimeProvider());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var search = service.SearchAsync("refund", 5, cts.Token);
        await generator.Started.WaitAsync(Ct);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
    }

    [Fact]
    public async Task A_reply_whose_retrieval_embedding_never_returns_ends_in_one_timeout_error()
    {
        var time = new TrackedFakeTime(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
        using var rig = Create(Turn(Text("never reached")), retrieval: Service(new StalledGenerator(), time), time: time);

        var events = await rig.RunAsync(Ticket(), Ct);

        StreamAssert.Error(events, ReplyErrorCodes.Timeout);
        Assert.Equal([events[^1]], events);
        Assert.Empty(rig.Spy.Calls);
    }

    private static RetrievalService Service(IEmbeddingGenerator<string, Embedding<float>> generator, TimeProvider time) =>
        new(new EmptyStore(), generator, Options.Create(new AiOptions()), Options.Create(new RetrievalOptions()), time);

    private sealed class StalledGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }

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
