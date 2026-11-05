using Microsoft.Extensions.AI;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Usage;
using SupportDesk.UnitTests.Telemetry;
using static SupportDesk.UnitTests.AI.ScriptedChatClient;

namespace SupportDesk.UnitTests.AI;

/// <summary>Recording a metric can't change a call's outcome (PLAN.md 14), even when a measurement listener throws.</summary>
[Collection(FakeClockGroup.Name)]
public class MetricsCannotChangeOutcomeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ChatOptions Options() =>
        new AiCallContext(AiFeature.Reply, "reply.v1", TicketId: 7, TicketNumber: "T-1007").ApplyTo(new ChatOptions());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_listener_that_throws_neither_drops_the_row_nor_changes_the_result(bool streaming)
    {
        var provider = new ScriptedChatClient(TimeProvider.System, Steps(Text("Hello"), Usage(10, 1)));
        using var pipeline = TestPipeline.Create(provider);
        using var listener = new ThrowingMeterListener(pipeline.Metrics.Factory);

        var text = "";
        if (streaming)
        {
            await foreach (var update in pipeline.Client.GetStreamingResponseAsync("hi", Options(), Ct))
            {
                text += update.Text;
            }
        }
        else
        {
            text = (await pipeline.Client.GetResponseAsync("hi", Options(), Ct)).Text;
        }

        Assert.Equal("Hello", text);
        var row = Assert.Single(pipeline.Rows.All);
        Assert.Equal((ModelCallOutcome.Success, 10L, 1L), (row.Outcome, row.InputTokens, row.OutputTokens));
    }

    [Fact]
    public async Task A_failed_call_still_fails_the_same_way_and_writes_its_row_when_a_listener_throws()
    {
        var provider = new ScriptedChatClient(TimeProvider.System, Steps(Fail(400)));
        using var pipeline = TestPipeline.Create(provider, classifier: new SupportDesk.Core.AI.Fake.FakeErrorClassifier());
        using var listener = new ThrowingMeterListener(pipeline.Metrics.Factory);

        await Assert.ThrowsAsync<ModelUnavailableException>(() => pipeline.Client.GetResponseAsync("hi", Options(), Ct));

        Assert.Equal(ModelCallOutcome.Error, Assert.Single(pipeline.Rows.All).Outcome);
    }

    [Fact]
    public void No_record_method_throws_when_a_listener_does()
    {
        using var metrics = new TestMetrics();
        using var listener = new ThrowingMeterListener(metrics.Factory);
        var row = new ModelCall { Provider = "fake", Model = "fake", PromptVersion = "reply.v1", Feature = AiFeature.Reply, CostUsd = 0.5m, InputTokens = 3 };

        var failures = new List<Exception>();
        foreach (var record in new Action[]
        {
            () => metrics.Metrics.RecordModelCall(row, new ModelUnavailableException("x", null, 503, isTransient: true)),
            () => metrics.Metrics.RecordDropped("buffer_full", 2),
            () => metrics.Metrics.RecordReply("done", guarded: true),
            () => metrics.Metrics.RecordToolCall("lookup_order_status", "ran"),
            () => metrics.Metrics.RecordGuardHits("reply", ["role turn", "system override"]),
            () => metrics.Metrics.RecordClassification("needs_manual", "invalid_output"),
            () => metrics.Metrics.RecordFollowUp("proposed"),
        })
        {
            var failure = Record.Exception(record);
            if (failure is not null)
            {
                failures.Add(failure);
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void An_unknown_outcome_is_a_programming_error_that_is_logged_and_never_reaches_the_call()
    {
        using var metrics = new TestMetrics();
        var row = new ModelCall { Provider = "fake", Model = "fake", PromptVersion = "reply.v1", Feature = AiFeature.Reply, Outcome = (ModelCallOutcome)99 };

        var failure = Record.Exception(() => metrics.Metrics.RecordModelCall(row, null));

        Assert.Null(failure);
        Assert.Empty(metrics.ModelCalls.GetMeasurementSnapshot());
    }
}
