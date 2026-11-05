using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Fake;
using SupportDesk.Core.AI.Usage;
using SupportDesk.Core.Replies;
using SupportDesk.Core.Telemetry;
using SupportDesk.UnitTests.Telemetry;
using static SupportDesk.UnitTests.AI.ScriptedChatClient;
using static SupportDesk.UnitTests.Telemetry.TestMetrics;

namespace SupportDesk.UnitTests.AI;

/// <summary>
/// The model-call counters (PLAN.md 14) as the pipeline records them: one count per <c>ModelCalls</c> row, from the row's own
/// values, so the counters and the table can't drift apart.
/// </summary>
[Collection(FakeClockGroup.Name)]
public class ModelCallMetricsTests
{
    private readonly TrackedFakeTime _time = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ChatOptions Options(string? model = null)
    {
        var options = new AiCallContext(AiFeature.Reply, "reply.v1", TicketId: 7, TicketNumber: "T-1007").ApplyTo(new ChatOptions());
        options.ModelId = model;
        return options;
    }

    [Fact]
    public async Task A_call_adds_one_model_call_and_its_rows_tokens_and_cost()
    {
        var provider = new ScriptedChatClient(_time, Steps(Text("x"), Usage(1000, 500)));
        using var pipeline = TestPipeline.Create(provider, _time);

        await pipeline.Client.GetResponseAsync("hi", Options(), Ct);

        var row = Assert.Single(pipeline.Rows.All);
        var m = pipeline.Metrics;
        Assert.Equal(1, Sum(m.ModelCalls, ("feature", "Reply"), ("model", row.Model), ("provider", row.Provider), ("outcome", "success")));
        Assert.Single(m.ModelCalls.GetMeasurementSnapshot());
        Assert.DoesNotContain(m.ModelCalls.GetMeasurementSnapshot(), x => x.Tags.ContainsKey("error.type"));
        Assert.Equal(row.InputTokens, Sum(m.Tokens, ("direction", "input"), ("feature", "Reply"), ("model", row.Model), ("estimated", false)));
        Assert.Equal(row.OutputTokens, Sum(m.Tokens, ("direction", "output"), ("estimated", false)));
        // 1000 x $4 + 500 x $20, per million tokens
        Assert.Equal(0.014, Sum(m.Cost, ("feature", "Reply"), ("model", row.Model)), 9);
    }

    [Fact]
    public async Task A_retried_call_adds_one_model_call_and_one_rows_tokens()
    {
        var provider = new ScriptedChatClient(_time, Steps(Fail(503)), Steps(Text("Hello"), Usage(10, 1)));
        using var pipeline = TestPipeline.Create(provider, _time, new FakeErrorClassifier());

        await _time.RunAsync(pipeline.Client.GetResponseAsync("hi", Options(), Ct));

        Assert.Equal(2, provider.Calls);
        Assert.Equal(1, Sum(pipeline.Metrics.ModelCalls));
        Assert.Equal(10, Sum(pipeline.Metrics.Tokens, ("direction", "input")));
        Assert.Equal(1, Sum(pipeline.Metrics.Tokens, ("direction", "output")));
    }

    [Fact]
    public async Task A_provider_failure_is_an_error_call_with_the_client_facing_code()
    {
        var provider = new ScriptedChatClient(_time, Steps(Fail(400)));
        using var pipeline = TestPipeline.Create(provider, _time, new FakeErrorClassifier());

        await Assert.ThrowsAsync<ModelUnavailableException>(() => pipeline.Client.GetResponseAsync("hi", Options(), Ct));

        Assert.Equal(1, Sum(pipeline.Metrics.ModelCalls, ("outcome", "error"), ("error.type", ReplyErrorCodes.ModelUnavailable)));
        Assert.Equal(1, Sum(pipeline.Metrics.ModelCalls));
    }

    [Fact]
    public async Task A_refusal_is_an_error_call_with_model_refused()
    {
        var refuse = Steps(_ => Task.FromResult<ChatResponseUpdate?>(new ChatResponseUpdate { Role = ChatRole.Assistant, FinishReason = ChatFinishReason.ContentFilter }));
        using var pipeline = TestPipeline.Create(new ScriptedChatClient(_time, refuse), _time, new FakeErrorClassifier());

        await Assert.ThrowsAsync<ModelRefusedException>(() => pipeline.Client.GetResponseAsync("hi", Options(), Ct));

        Assert.Equal(1, Sum(pipeline.Metrics.ModelCalls, ("outcome", "error"), ("error.type", ReplyErrorCodes.ModelRefused)));
    }

    [Fact]
    public async Task A_timeout_is_an_error_call_with_timeout()
    {
        var provider = new ScriptedChatClient(_time, Steps(Text("Sorry about "), Wait(_time, TimeSpan.FromSeconds(30)), Text("that.")));
        using var pipeline = TestPipeline.Create(provider, _time, new FakeErrorClassifier());

        await _time.RunAsync(Assert.ThrowsAsync<ModelUnavailableException>(async () =>
        {
            await foreach (var update in pipeline.Client.GetStreamingResponseAsync("hi", Options(), Ct))
            {
                Assert.NotNull(update);
            }
        }));

        Assert.Equal(1, Sum(pipeline.Metrics.ModelCalls, ("outcome", "error"), ("error.type", ReplyErrorCodes.Timeout)));
    }

    [Fact]
    public async Task A_stream_the_caller_stops_reading_is_cancelled_not_an_error()
    {
        var provider = new ScriptedChatClient(_time, Steps(Text("Hello there"), Text(" more"), Usage(10, 3)));
        using var pipeline = TestPipeline.Create(provider, _time);

        await foreach (var update in pipeline.Client.GetStreamingResponseAsync("hi", Options(), Ct))
        {
            Assert.NotNull(update);
            break;
        }

        var calls = pipeline.Metrics.ModelCalls.GetMeasurementSnapshot();
        var call = Assert.Single(calls);
        Assert.Equal("cancelled", call.Tags["outcome"]);
        Assert.False(call.Tags.ContainsKey("error.type"));
        // The row is an estimate, and the counters say so
        Assert.True(Sum(pipeline.Metrics.Tokens, ("direction", "input"), ("estimated", true)) > 0);
        Assert.Equal(0, Sum(pipeline.Metrics.Tokens, ("estimated", false)));
    }

    [Fact]
    public async Task An_unpriced_model_adds_tokens_but_no_cost()
    {
        var provider = new ScriptedChatClient(_time, Steps(Text("x"), Usage(1000, 500)));
        using var pipeline = TestPipeline.Create(provider, _time);

        await pipeline.Client.GetResponseAsync("hi", Options("mystery-model"), Ct);

        Assert.Null(Assert.Single(pipeline.Rows.All).CostUsd);
        Assert.Equal(1000, Sum(pipeline.Metrics.Tokens, ("direction", "input"), ("model", "mystery-model")));
        Assert.Empty(pipeline.Metrics.Cost.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task A_row_the_sink_loses_is_still_counted()
    {
        var provider = new ScriptedChatClient(_time, Steps(Text("Hello"), Usage(10, 1)));
        using var pipeline = TestPipeline.Create(provider, _time, sink: new ThrowingSink());

        await pipeline.Client.GetResponseAsync("hi", Options(), Ct);

        Assert.Equal(1, Sum(pipeline.Metrics.ModelCalls));
    }

    [Fact]
    public void A_full_buffer_adds_a_buffer_full_drop_and_a_closed_one_adds_a_shutdown_drop()
    {
        using var metrics = new TestMetrics();
        var buffer = new ModelCallBuffer(
            Microsoft.Extensions.Options.Options.Create(TestPipeline.Settings(o => o.Usage.QueueCapacity = 1)),
            metrics.Metrics,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelCallBuffer>.Instance);

        buffer.Add(Row());
        buffer.Add(Row());
        Assert.Equal(1, Sum(metrics.Dropped, ("reason", "buffer_full")));

        buffer.Reader.TryRead(out _);
        buffer.Complete();
        buffer.Add(Row());

        Assert.Equal(1, Sum(metrics.Dropped, ("reason", "shutdown")));
        Assert.Equal(1, Sum(metrics.Dropped, ("reason", "buffer_full")));
    }

    private static ModelCall Row() => new() { Provider = "fake", Model = "fake", PromptVersion = "reply.v1", Feature = AiFeature.Reply };

    private sealed class ThrowingSink : IModelCallSink
    {
        public void Add(ModelCall row) => throw new InvalidOperationException("sink is broken");
    }
}
