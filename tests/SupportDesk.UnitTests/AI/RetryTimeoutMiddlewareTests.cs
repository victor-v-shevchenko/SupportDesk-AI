using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Fake;
using SupportDesk.Core.AI.Usage;
using static SupportDesk.UnitTests.AI.ScriptedChatClient;

namespace SupportDesk.UnitTests.AI;

/// <summary>Retries and timeouts (PLAN.md 6.5), through the whole pipeline on a fake clock.</summary>
public class RetryTimeoutMiddlewareTests
{
    private readonly FakeTimeProvider _time = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ChatOptions Reply => new AiCallContext(AiFeature.Reply, "reply.v1", TicketId: 1, TicketNumber: "T-1001").ApplyTo(new ChatOptions());

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task A_transient_error_before_the_first_item_is_retried_up_to_the_limit_then_surfaces(int status)
    {
        var provider = new ScriptedChatClient(_time, Steps(Fail(status)));
        using var pipeline = Pipeline(provider);
        var received = new List<ChatResponseUpdate>();

        var error = await Assert.ThrowsAsync<ModelUnavailableException>(() => _time.ReadAllAsync(pipeline.Client.GetStreamingResponseAsync("hi", Reply, Ct), received));

        Assert.Equal(3, provider.Calls);
        Assert.Equal(status, error.StatusCode);
        Assert.Empty(received);
    }

    [Fact]
    public async Task A_transient_error_then_a_good_attempt_gives_the_good_answer_and_nothing_from_the_failed_one()
    {
        // The failed attempt sends an empty update and usage before failing; neither may reach the caller
        var provider = new ScriptedChatClient(_time, Steps(Empty(), Usage(100, 0), Fail(529)), Steps(Text("Hello"), Usage(120, 1)));
        using var pipeline = Pipeline(provider);
        var received = new List<ChatResponseUpdate>();

        await _time.ReadAllAsync(pipeline.Client.GetStreamingResponseAsync("hi", Reply, Ct), received);

        Assert.Equal(2, provider.Calls);
        Assert.Equal("Hello", string.Concat(received.Select(u => u.Text)));
        Assert.Equal(120, Assert.Single(received.SelectMany(u => u.Contents).OfType<UsageContent>()).Details.InputTokenCount);
    }

    [Fact]
    public async Task A_non_streaming_call_is_retried_too()
    {
        var provider = new ScriptedChatClient(_time, Steps(Fail(503)), Steps(Text("Hello")));
        using var pipeline = Pipeline(provider);

        var response = await _time.RunAsync(pipeline.Client.GetResponseAsync("hi", Reply, Ct));

        Assert.Equal("Hello", response.Text);
        Assert.Equal(2, provider.Calls);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    public async Task A_client_error_is_not_retried(int status)
    {
        var provider = new ScriptedChatClient(_time, Steps(Fail(status)));
        using var pipeline = Pipeline(provider);

        var error = await Assert.ThrowsAsync<ModelUnavailableException>(() => _time.RunAsync(pipeline.Client.GetResponseAsync("hi", Reply, Ct)));

        Assert.Equal(1, provider.Calls);
        Assert.False(error.IsTransient);
    }

    [Fact]
    public async Task An_error_after_the_first_item_is_not_retried_and_comes_after_the_items_already_sent()
    {
        var provider = new ScriptedChatClient(_time, Steps(Text("Hel"), Fail(503)));
        using var pipeline = Pipeline(provider);
        var received = new List<ChatResponseUpdate>();

        var error = await Assert.ThrowsAsync<ModelUnavailableException>(() => _time.ReadAllAsync(pipeline.Client.GetStreamingResponseAsync("hi", Reply, Ct), received));

        Assert.Equal(1, provider.Calls);
        Assert.Equal("Hel", string.Concat(received.Select(u => u.Text)));
        Assert.Equal(503, error.StatusCode);
    }

    [Fact]
    public async Task A_refusal_is_not_retried()
    {
        var provider = new ScriptedChatClient(_time, _ => Refusal());
        using var pipeline = Pipeline(provider);

        await Assert.ThrowsAsync<ModelRefusedException>(() => _time.RunAsync(pipeline.Client.GetResponseAsync("hi", Reply, Ct)));

        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Backoff_doubles_with_jitter_in_the_upper_half()
    {
        var provider = new ScriptedChatClient(_time, Steps(Fail(503)));
        using var pipeline = Pipeline(provider, o => o.Retry.MaxAttempts = 3);

        await Assert.ThrowsAsync<ModelUnavailableException>(() => _time.RunAsync(pipeline.Client.GetResponseAsync("hi", Reply, Ct), TimeSpan.FromMilliseconds(10)));

        var times = provider.CallTimes;
        // Base 500 ms: the first wait is 250–500 ms, the second 500–1000 ms (plus at most one clock step)
        Assert.InRange((times[1] - times[0]).TotalMilliseconds, 250, 510);
        Assert.InRange((times[2] - times[1]).TotalMilliseconds, 500, 1010);
    }

    [Fact]
    public async Task Retry_After_is_waited_for_instead_of_the_backoff()
    {
        var provider = new ScriptedChatClient(_time, Steps(Fail(429, retryAfterSeconds: 4)), Steps(Text("Hello")));
        using var pipeline = Pipeline(provider);

        await _time.RunAsync(pipeline.Client.GetResponseAsync("hi", Reply, Ct), TimeSpan.FromMilliseconds(50));

        Assert.InRange((provider.CallTimes[1] - provider.CallTimes[0]).TotalSeconds, 4, 4.05);
    }

    [Fact]
    public async Task A_Retry_After_longer_than_the_max_delay_ends_the_call_instead_of_waiting()
    {
        var provider = new ScriptedChatClient(_time, Steps(Fail(429, retryAfterSeconds: 60)));
        using var pipeline = Pipeline(provider);

        var error = await Assert.ThrowsAsync<ModelUnavailableException>(() => _time.RunAsync(pipeline.Client.GetResponseAsync("hi", Reply, Ct)));

        Assert.Equal(1, provider.Calls);
        Assert.Equal(TimeSpan.FromSeconds(60), error.RetryAfter);
    }

    [Fact]
    public async Task A_first_item_later_than_FirstItemSeconds_times_out_and_is_retried()
    {
        var provider = new ScriptedChatClient(_time, Steps(Empty(), Wait(_time, TimeSpan.FromSeconds(25)), Text("late")));
        using var pipeline = Pipeline(provider);
        var received = new List<ChatResponseUpdate>();

        var error = await Assert.ThrowsAsync<ModelUnavailableException>(() =>
            _time.ReadAllAsync(pipeline.Client.GetStreamingResponseAsync("hi", Reply, Ct), received, TimeSpan.FromMilliseconds(250)));

        Assert.True(error.IsTimeout);
        Assert.Contains("first item", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, provider.Calls);
        Assert.Empty(received);
        // Each attempt gave up at 20 s, not 25: the empty update didn't restart the first-item timer
        Assert.InRange((provider.CallTimes[1] - provider.CallTimes[0]).TotalSeconds, 20.2, 21);
    }

    [Fact]
    public async Task A_gap_longer_than_GapSeconds_ends_the_call_without_a_retry()
    {
        var provider = new ScriptedChatClient(_time, Steps(Text("Hel"), Wait(_time, TimeSpan.FromSeconds(16)), Text("lo")));
        using var pipeline = Pipeline(provider);
        var received = new List<ChatResponseUpdate>();

        var error = await Assert.ThrowsAsync<ModelUnavailableException>(() =>
            _time.ReadAllAsync(pipeline.Client.GetStreamingResponseAsync("hi", Reply, Ct), received, TimeSpan.FromMilliseconds(250)));

        Assert.True(error.IsTimeout);
        Assert.Contains("next update", error.Message, StringComparison.Ordinal);
        Assert.Equal("Hel", string.Concat(received.Select(u => u.Text)));
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Gaps_shorter_than_GapSeconds_are_fine_however_long_the_stream()
    {
        var provider = new ScriptedChatClient(_time, Steps(
            Text("a"), Wait(_time, TimeSpan.FromSeconds(14)), Text("b"), Wait(_time, TimeSpan.FromSeconds(14)), Text("c")));
        using var pipeline = Pipeline(provider);
        var received = new List<ChatResponseUpdate>();

        await _time.ReadAllAsync(pipeline.Client.GetStreamingResponseAsync("hi", Reply, Ct), received, TimeSpan.FromMilliseconds(250));

        Assert.Equal("abc", string.Concat(received.Select(u => u.Text)));
    }

    [Fact]
    public async Task The_callers_own_time_between_reads_does_not_count_as_a_gap()
    {
        var provider = new ScriptedChatClient(_time, Steps(Text("Hel"), Text("lo")));
        using var pipeline = Pipeline(provider);
        await using var updates = pipeline.Client.GetStreamingResponseAsync("hi", Reply, Ct).GetAsyncEnumerator(Ct);

        Assert.True(await _time.RunAsync(updates.MoveNextAsync().AsTask()));
        _time.Advance(TimeSpan.FromSeconds(60));

        Assert.True(await _time.RunAsync(updates.MoveNextAsync().AsTask()));
        Assert.Equal("lo", updates.Current.Text);
    }

    [Fact]
    public async Task The_callers_time_with_the_first_item_does_not_count_against_the_first_item_timeout()
    {
        var provider = new ScriptedChatClient(_time, Steps(Text("Hel"), Text("lo")));
        using var pipeline = Pipeline(provider);
        await using var updates = pipeline.Client.GetStreamingResponseAsync("hi", Reply, Ct).GetAsyncEnumerator(Ct);

        Assert.True(await _time.RunAsync(updates.MoveNextAsync().AsTask()));
        // Longer than FirstItemSeconds (20 s) but within GapSeconds' reach once the timer is reset: the caller is just slow
        _time.Advance(TimeSpan.FromSeconds(25));

        Assert.True(await _time.RunAsync(updates.MoveNextAsync().AsTask()));
        Assert.Equal("lo", updates.Current.Text);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task A_stream_longer_than_StreamTotalSeconds_ends_as_a_timeout()
    {
        var steps = Enumerable.Range(0, 20).SelectMany(_ => new[] { Text("x"), Wait(_time, TimeSpan.FromSeconds(10)) }).ToArray();
        var provider = new ScriptedChatClient(_time, Steps(steps));
        using var pipeline = Pipeline(provider);
        var received = new List<ChatResponseUpdate>();

        var error = await Assert.ThrowsAsync<ModelUnavailableException>(() =>
            _time.ReadAllAsync(pipeline.Client.GetStreamingResponseAsync("hi", Reply, Ct), received, TimeSpan.FromMilliseconds(500)));

        Assert.True(error.IsTimeout);
        Assert.Contains("whole stream", error.Message, StringComparison.Ordinal);
        Assert.Equal(12, received.Count(u => u.Text == "x"));
    }

    [Fact]
    public async Task A_non_streaming_call_longer_than_NonStreamingSeconds_times_out()
    {
        var provider = new ScriptedChatClient(_time, Steps(Wait(_time, TimeSpan.FromSeconds(40)), Text("late")));
        using var pipeline = Pipeline(provider, o => o.Retry.MaxAttempts = 1);

        var error = await Assert.ThrowsAsync<ModelUnavailableException>(() => _time.RunAsync(pipeline.Client.GetResponseAsync("hi", Reply, Ct), TimeSpan.FromMilliseconds(250)));

        Assert.True(error.IsTimeout);
        Assert.InRange((_time.GetUtcNow() - provider.CallTimes[0]).TotalSeconds, 30, 31);
    }

    [Fact]
    public async Task Caller_cancellation_stops_the_call_at_once_with_no_retry_and_logs_it_as_cancelled()
    {
        var provider = new ScriptedChatClient(_time, Steps(Wait(_time, TimeSpan.FromMinutes(5)), Text("never")));
        using var pipeline = Pipeline(provider);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var updates = pipeline.Client.GetStreamingResponseAsync("hi", Reply, cancellation.Token).GetAsyncEnumerator(cancellation.Token);

        var next = updates.MoveNextAsync().AsTask();
        _time.Advance(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        // No clock movement needed: cancellation alone ends it
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal(1, provider.Calls);
        Assert.Equal(ModelCallOutcome.Cancelled, Assert.Single(pipeline.Rows.All).Outcome);
    }

    [Fact]
    public async Task Cancelling_during_the_wait_before_a_retry_stops_without_another_attempt()
    {
        var provider = new ScriptedChatClient(_time, Steps(Fail(429, retryAfterSeconds: 5)), Steps(Text("Hello")));
        using var pipeline = Pipeline(provider);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var call = pipeline.Client.GetResponseAsync("hi", Reply, cancellation.Token);
        _time.Advance(TimeSpan.FromSeconds(1));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, provider.Calls);
    }

    private TestPipeline Pipeline(ScriptedChatClient provider, Action<AiOptions>? configure = null) =>
        TestPipeline.Create(provider, _time, new FakeErrorClassifier(), configure);

    private static async IAsyncEnumerable<ChatResponseUpdate> Refusal()
    {
        await Task.Yield();
        yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.ContentFilter };
    }
}
