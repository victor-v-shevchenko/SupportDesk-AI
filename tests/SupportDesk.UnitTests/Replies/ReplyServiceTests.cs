using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Prompts;
using SupportDesk.Core.AI.Usage;
using SupportDesk.Core.Replies;
using SupportDesk.Core.Retrieval;
using SupportDesk.UnitTests.AI;
using static SupportDesk.UnitTests.AI.ScriptedChatClient;
using static SupportDesk.UnitTests.Replies.ReplyTestData;

namespace SupportDesk.UnitTests.Replies;

public sealed class ReplyServiceTests : IDisposable
{
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;
    private TestPipeline? _pipeline;

    public ReplyServiceTests()
    {
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(_logs));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_reply_streams_tokens_then_citations_usage_and_done()
    {
        var provider = new ScriptedChatClient(TimeProvider.System, Steps(Text("Ships in 3 days [1]"), Text(". Returns take 30 days [2]."), Usage(100, 20)));
        var retrieval = new StubRetrieval(Chunk("shipping-times", 0.8, title: "Shipping times"), Chunk("returns-policy", 0.7, title: "Returns policy"));
        var service = Service(provider, retrieval);
        var ticket = Ticket(number: "T-1001", id: 7);

        var events = await ReadAllAsync(service.StreamAsync(ticket, Ct));

        Assert.Equal(
            [typeof(ReplyToken), typeof(ReplyToken), typeof(ReplyCitations), typeof(ReplyUsage), typeof(ReplyDone)],
            events.Select(e => e.GetType()));
        Assert.Equal("Ships in 3 days [1]. Returns take 30 days [2].", string.Concat(events.OfType<ReplyToken>().Select(t => t.Text)));
        var citations = Assert.IsType<ReplyCitations>(events[2]);
        Assert.Equal([new Citation(1, "shipping-times", "Shipping times"), new Citation(2, "returns-policy", "Returns policy")], citations.Citations);
        Assert.Empty(citations.Removed);
        // claude-opus-5-5 at 4 / 20 USD per million tokens
        Assert.Equal(new ReplyUsage(100, 20, 0.0008m, Estimated: false), events[3]);

        var done = Assert.IsType<ReplyDone>(events[4]);
        var context = AiCallContext.From(provider.LastOptions);
        Assert.Equal(new AiCallContext(AiFeature.Reply, "reply.v1", 7, "T-1001", done.ReplyRunId), context);
        Assert.NotEqual(Guid.Empty, done.ReplyRunId);
        var row = Assert.Single(_pipeline!.Rows.All);
        Assert.Equal(done.ReplyRunId, row.ReplyRunId);
        Assert.Equal(ModelCallOutcome.Success, row.Outcome);

        var call = Assert.Single(retrieval.Calls);
        Assert.Equal(ReplyPromptBuilder.BuildQuery(ticket), call.Query);
        Assert.Equal(5, call.K);
    }

    [Fact]
    public async Task Each_reply_gets_a_new_run_id()
    {
        var service = Service(new ScriptedChatClient(TimeProvider.System, Steps(Text("Hi."))), new StubRetrieval());

        var first = (await ReadAllAsync(service.StreamAsync(Ticket(), Ct))).OfType<ReplyDone>().Single();
        var second = (await ReadAllAsync(service.StreamAsync(Ticket(), Ct))).OfType<ReplyDone>().Single();

        Assert.NotEqual(first.ReplyRunId, second.ReplyRunId);
    }

    [Fact]
    public async Task Out_of_range_markers_are_removed_and_logged_without_the_reply_text()
    {
        var service = Service(new ScriptedChatClient(TimeProvider.System, Steps(Text("Secret detail [1] and [4]."))), new StubRetrieval(Chunk("shipping-times", 0.8)));

        var events = await ReadAllAsync(service.StreamAsync(Ticket(), Ct));

        var citations = events.OfType<ReplyCitations>().Single();
        Assert.Equal([1], citations.Citations.Select(c => c.Marker));
        Assert.Equal([4], citations.Removed);
        var log = Assert.Single(_logs.Messages, m => m.Contains("cited markers outside 1…1: 4", StringComparison.Ordinal));
        Assert.DoesNotContain("Secret detail", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Usage_is_estimated_when_the_provider_sends_none()
    {
        var service = Service(new ScriptedChatClient(TimeProvider.System, Steps(Text("0123456789"))), new StubRetrieval());

        var usage = (await ReadAllAsync(service.StreamAsync(Ticket(), Ct))).OfType<ReplyUsage>().Single();

        Assert.True(usage.Estimated);
        Assert.Equal(4, usage.OutputTokens);
        Assert.True(usage.InputTokens > 0);
    }

    [Fact]
    public async Task Thinking_and_tool_calls_are_not_sent_as_tokens()
    {
        static Func<CancellationToken, Task<ChatResponseUpdate?>> Content(AIContent content) =>
            _ => Task.FromResult<ChatResponseUpdate?>(new ChatResponseUpdate(ChatRole.Assistant, [content]));
        var service = Service(
            new ScriptedChatClient(TimeProvider.System, Steps(Content(new TextReasoningContent("Let me think")), Empty(), Text("Answer."))),
            new StubRetrieval());

        var events = await ReadAllAsync(service.StreamAsync(Ticket(), Ct));

        Assert.Equal("Answer.", Assert.Single(events.OfType<ReplyToken>()).Text);
    }

    [Fact]
    public async Task An_error_before_the_first_token_is_the_only_event()
    {
        var service = Service(new ScriptedChatClient(TimeProvider.System, Steps(Fail(503))), new StubRetrieval(), o => o.Retry.MaxAttempts = 1);

        var events = await ReadAllAsync(service.StreamAsync(Ticket(), Ct));

        Assert.Equal(ReplyErrorCodes.ModelUnavailable, Assert.IsType<ReplyError>(Assert.Single(events)).Code);
        Assert.Contains(_logs.Messages, m => m.Contains("failed with model_unavailable: ModelUnavailableException (status 503)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_error_after_tokens_ends_the_stream_without_citations_usage_or_done()
    {
        var service = Service(new ScriptedChatClient(TimeProvider.System, Steps(Text("Thanks for "), Fail(529))), new StubRetrieval());

        var events = await ReadAllAsync(service.StreamAsync(Ticket(), Ct));

        Assert.Equal([typeof(ReplyToken), typeof(ReplyError)], events.Select(e => e.GetType()));
        Assert.Equal(ReplyErrorCodes.ModelUnavailable, ((ReplyError)events[1]).Code);
    }

    [Fact]
    public async Task A_refusal_gives_model_refused()
    {
        var refuse = Steps(_ => Task.FromResult<ChatResponseUpdate?>(new ChatResponseUpdate { Role = ChatRole.Assistant, FinishReason = ChatFinishReason.ContentFilter }));
        var service = Service(new ScriptedChatClient(TimeProvider.System, refuse), new StubRetrieval());

        var events = await ReadAllAsync(service.StreamAsync(Ticket(), Ct));

        Assert.Equal(ReplyErrorCodes.ModelRefused, Assert.IsType<ReplyError>(Assert.Single(events)).Code);
    }

    [Fact]
    public async Task A_prompt_over_the_budget_gives_budget_exceeded_without_calling_the_provider()
    {
        var provider = new ScriptedChatClient(TimeProvider.System, Steps(Text("never")));
        var service = Service(provider, new StubRetrieval(Chunk("a", 0.9)), o => o.Budget.MaxInputTokensPerCall = 50);

        var events = await ReadAllAsync(service.StreamAsync(Ticket(), Ct));

        Assert.Equal(ReplyErrorCodes.BudgetExceeded, Assert.IsType<ReplyError>(Assert.Single(events)).Code);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task A_stall_after_the_first_token_gives_timeout()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var provider = new ScriptedChatClient(time, Steps(Text("Sorry about "), Wait(time, TimeSpan.FromSeconds(30)), Text("that.")));
        var service = Service(provider, new StubRetrieval(), time: time);

        var events = await time.RunAsync(ReadAllAsync(service.StreamAsync(Ticket(), Ct)), TimeSpan.FromSeconds(1));

        Assert.Equal([typeof(ReplyToken), typeof(ReplyError)], events.Select(e => e.GetType()));
        Assert.Equal(ReplyErrorCodes.Timeout, ((ReplyError)events[1]).Code);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task A_failed_retrieval_is_an_error_event()
    {
        var service = Service(
            new ScriptedChatClient(TimeProvider.System, Steps(Text("never"))),
            new StubRetrieval { Throw = new ModelUnavailableException("Embeddings failed.", null, 503, isTransient: true) });

        var events = await ReadAllAsync(service.StreamAsync(Ticket(), Ct));

        Assert.Equal(ReplyErrorCodes.ModelUnavailable, Assert.IsType<ReplyError>(Assert.Single(events)).Code);
    }

    [Fact]
    public async Task An_unexpected_failure_is_an_internal_error_and_logged()
    {
        var service = Service(
            new ScriptedChatClient(TimeProvider.System, Steps(Text("never"))),
            new StubRetrieval { Throw = new InvalidOperationException("boom") });

        var events = await ReadAllAsync(service.StreamAsync(Ticket(), Ct));

        var error = Assert.IsType<ReplyError>(Assert.Single(events));
        Assert.Equal(ReplyErrorCodes.InternalError, error.Code);
        Assert.DoesNotContain("boom", error.Message, StringComparison.Ordinal);
        Assert.Contains(_logs.Messages, m => m.Contains("failed unexpectedly", StringComparison.Ordinal) && m.Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelling_ends_the_stream_without_an_error_event_and_logs_the_call_as_cancelled()
    {
        var provider = new ScriptedChatClient(TimeProvider.System, Steps(Text("First "), Wait(TimeProvider.System, TimeSpan.FromSeconds(10)), Text("never")));
        var service = Service(provider, new StubRetrieval());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var received = new List<ReplyEvent>();

        // The pipeline's own linked tokens carry the cancellation, so the exception's token isn't cts.Token itself
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var reply in service.StreamAsync(Ticket(), cts.Token))
            {
                received.Add(reply);
                await cts.CancelAsync();
            }
        });

        Assert.IsType<ReplyToken>(Assert.Single(received));
        Assert.Equal(ModelCallOutcome.Cancelled, Assert.Single(_pipeline!.Rows.All).Outcome);
        Assert.Contains(_logs.Messages, m => m.Contains("was cancelled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stopping_reading_cancels_the_call()
    {
        var provider = new ScriptedChatClient(TimeProvider.System, Steps(Text("First "), Text("second")));
        var service = Service(provider, new StubRetrieval());

        await using (var events = service.StreamAsync(Ticket(), Ct).GetAsyncEnumerator(Ct))
        {
            Assert.True(await events.MoveNextAsync());
        }

        Assert.Equal(ModelCallOutcome.Cancelled, Assert.Single(_pipeline!.Rows.All).Outcome);
        Assert.Contains(_logs.Messages, m => m.Contains("was cancelled", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        _pipeline?.Dispose();
        _loggerFactory.Dispose();
        _logs.Dispose();
    }

    private ReplyService Service(IChatClient provider, IRetrievalService retrieval, Action<AiOptions>? configure = null, TimeProvider? time = null)
    {
        void Configure(AiOptions options)
        {
            options.Prompts["reply"] = "reply.v1";
            configure?.Invoke(options);
        }

        _pipeline = TestPipeline.Create(provider, time, configure: Configure);
        var options = Options.Create(TestPipeline.Settings(Configure));
        return new ReplyService(
            _pipeline.Client,
            retrieval,
            new PromptStore(options),
            new PriceTable(options, NullLogger<PriceTable>.Instance),
            options,
            Options.Create(new RetrievalOptions()),
            _loggerFactory.CreateLogger<ReplyService>());
    }

    private static async Task<List<ReplyEvent>> ReadAllAsync(IAsyncEnumerable<ReplyEvent> events)
    {
        var list = new List<ReplyEvent>();
        await foreach (var reply in events)
        {
            list.Add(reply);
        }

        return list;
    }
}
