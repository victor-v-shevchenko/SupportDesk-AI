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
        await generator.Started.WaitAsync(Wait, Ct);
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(search.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<ModelUnavailableException>(() => search.WaitAsync(Wait, Ct));
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
        await generator.Started.WaitAsync(Wait, Ct);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.WaitAsync(Wait, Ct));
    }

    [Fact]
    public async Task A_reply_whose_retrieval_embedding_never_returns_ends_in_one_timeout_error()
    {
        var time = new TrackedFakeTime(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
        using var rig = Create(Turn(Text("never reached")), retrieval: Service(new StalledGenerator(), time), time: time);

        var events = await rig.RunAsync(Ticket(), Ct).WaitAsync(Wait, Ct);

        StreamAssert.Error(events, ReplyErrorCodes.Timeout);
        Assert.Equal([events[^1]], events);
        Assert.Empty(rig.Spy.Calls);
    }

    [Fact]
    public async Task A_caller_who_cancelled_after_the_limit_expired_gets_a_cancellation_not_a_timeout()
    {
        var time = new FakeTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        // The call only looks at its token after the gate opens, by which time both the limit and the caller have fired
        var call = EmbeddingTimeout.RunAsync(time, 30, async token =>
        {
            started.SetResult();
            await gate.Task;
            token.ThrowIfCancellationRequested();
            return 0;
        }, cts.Token);
        await started.Task.WaitAsync(Wait, Ct);
        time.Advance(TimeSpan.FromSeconds(30));
        await cts.CancelAsync();
        gate.SetResult();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Wait, Ct));
        Assert.IsNotType<TimeoutException>(exception);
    }

    [Fact]
    public async Task A_failure_after_the_caller_cancelled_is_a_cancellation_not_a_model_error()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var service = Service(new FailingGenerator(new TimeoutException("fired just before the cancel")), new FakeTimeProvider());

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchAsync("refund", 5, cts.Token));
        Assert.IsNotType<ModelUnavailableException>(exception);
    }

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class FailingGenerator(Exception failure) : IEmbeddingGenerator<string, Embedding<float>>
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
