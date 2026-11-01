using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Fake;
using SupportDesk.Core.AI.Usage;
using static SupportDesk.UnitTests.AI.ScriptedChatClient;

namespace SupportDesk.UnitTests.AI;

/// <summary>The token budget (PLAN.md 6.4), usage rows and cost (6.6), and the middleware order (6.2).</summary>
public class BudgetAndUsageTests
{
    private const string TicketText = "SECRET-TICKET-TEXT-7f3a";

    private readonly FakeTimeProvider _time = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ChatOptions Options(AiFeature feature = AiFeature.Reply, Guid? replyRunId = null) =>
        new AiCallContext(feature, "reply.v1", TicketId: 7, TicketNumber: "T-1007", ReplyRunId: replyRunId).ApplyTo(new ChatOptions());

    [Fact]
    public void The_middleware_order_matches_the_plan()
    {
        using var pipeline = TestPipeline.Create(new ScriptedChatClient(_time, Steps(Text("x"))), _time);

        var chain = new List<Type>();
        for (IChatClient? client = pipeline.Client; client is not null; client = InnerOf(client))
        {
            chain.Add(client.GetType());
        }

        Assert.Equal(
            [
                typeof(RequestCapMiddleware),
                typeof(FunctionInvokingChatClient),
                typeof(RequestCapCounterMiddleware),
                typeof(BudgetGuardMiddleware),
                typeof(UsageLoggingMiddleware),
                typeof(RetryTimeoutMiddleware),
                typeof(ErrorMappingMiddleware),
                typeof(ScriptedChatClient),
            ],
            chain);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 2)]
    [InlineData(10, 4)]
    [InlineData(100, 40)]
    [InlineData(101, 41)]
    public void The_estimate_is_characters_over_3_times_1_2_rounded_up(long characters, long tokens) =>
        Assert.Equal(tokens, TokenEstimator.FromCharacters(characters));

    [Fact]
    public void Each_message_adds_10_tokens_for_its_framing()
    {
        ChatMessage[] messages = [new(ChatRole.System, "abc"), new(ChatRole.User, "")];

        // 3 characters is 2 tokens, plus 10 for each of the two messages
        Assert.Equal(22, TokenEstimator.EstimateInput(messages, options: null));
    }

    [Fact]
    public async Task An_oversized_call_is_refused_before_it_reaches_the_provider()
    {
        var provider = new ScriptedChatClient(_time, Steps(Text("x")));
        using var pipeline = TestPipeline.Create(provider, _time, configure: o => o.Budget.MaxInputTokensPerCall = 100);

        // 400 characters is 160 tokens by the estimate, plus 10 for the message
        var error = await Assert.ThrowsAsync<BudgetExceededException>(() => pipeline.Client.GetResponseAsync(new string('a', 400), Options(), Ct));

        Assert.Equal(170, error.Tokens);
        Assert.Equal(100, error.Limit);
        Assert.Equal(0, provider.Calls);
        Assert.Empty(pipeline.Rows.All);
    }

    [Fact]
    public async Task Tool_definitions_count_toward_the_input_estimate()
    {
        var provider = new ScriptedChatClient(_time, Steps(Text("x")));
        using var pipeline = TestPipeline.Create(provider, _time, configure: o => o.Budget.MaxInputTokensPerCall = 100);
        var options = Options();
        options.Tools = [AIFunctionFactory.Create((string orderNumber) => "ok", "lookup_order_status", new string('d', 400))];

        await Assert.ThrowsAsync<BudgetExceededException>(() => pipeline.Client.GetResponseAsync("hi", options, Ct));
    }

    [Theory]
    [InlineData(AiFeature.Reply, 800)]
    [InlineData(AiFeature.Summary, 200)]
    [InlineData(AiFeature.Classification, 200)]
    public async Task The_feature_output_limit_is_set_when_the_caller_did_not(AiFeature feature, int expected)
    {
        var provider = new ScriptedChatClient(_time, Steps(Text("x")));
        using var pipeline = TestPipeline.Create(provider, _time);

        await pipeline.Client.GetResponseAsync("hi", Options(feature), Ct);

        Assert.Equal(expected, provider.LastOptions?.MaxOutputTokens);
    }

    [Fact]
    public async Task The_callers_output_limit_is_kept()
    {
        var provider = new ScriptedChatClient(_time, Steps(Text("x")));
        using var pipeline = TestPipeline.Create(provider, _time);
        var options = Options();
        options.MaxOutputTokens = 50;

        await pipeline.Client.GetResponseAsync("hi", options, Ct);

        Assert.Equal(50, provider.LastOptions?.MaxOutputTokens);
    }

    [Fact]
    public async Task The_request_cap_stops_a_tool_loop_whose_total_passes_it()
    {
        // Every call asks for the tool again and reports 6000 tokens; the cap is 10000, so the third call is refused
        var provider = new ScriptedChatClient(_time, _ => ToolCallWithUsage(6000));
        using var pipeline = TestPipeline.Create(provider, _time, configure: o => o.Budget.MaxTokensPerRequest = 10_000);
        var options = Options();
        options.Tools = [AIFunctionFactory.Create(() => "ok", "lookup_order_status")];

        var error = await Assert.ThrowsAsync<BudgetExceededException>(() => pipeline.Client.GetResponseAsync("hi", options, Ct));

        Assert.Equal(2, provider.Calls);
        Assert.Equal(12_000, error.Tokens);
        Assert.Equal(2, pipeline.Rows.All.Count);
    }

    [Fact]
    public async Task Each_request_gets_its_own_tally()
    {
        var provider = new ScriptedChatClient(_time, Steps(Text("x"), Usage(6000, 0)));
        using var pipeline = TestPipeline.Create(provider, _time, configure: o => o.Budget.MaxTokensPerRequest = 10_000);
        var options = Options();

        // Reusing one options object, as a feature might
        for (var i = 0; i < 3; i++)
        {
            await pipeline.Client.GetResponseAsync("hi", options, Ct);
        }

        Assert.Equal(3, provider.Calls);
    }

    [Fact]
    public async Task Every_inner_call_writes_one_row_with_the_feature_prompt_version_and_ticket()
    {
        var provider = new ScriptedChatClient(_time, _ => ToolCallWithUsage(100), Steps(Text("done"), Usage(150, 20)));
        using var pipeline = TestPipeline.Create(provider, _time);
        var replyRunId = Guid.NewGuid();
        var options = Options(AiFeature.Reply, replyRunId);
        options.Tools = [AIFunctionFactory.Create(() => "ok", "lookup_order_status")];

        await pipeline.Client.GetResponseAsync("hi", options, Ct);

        var rows = pipeline.Rows.All;
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(AiFeature.Reply, row.Feature);
            Assert.Equal("reply.v1", row.PromptVersion);
            Assert.Equal(7, row.TicketId);
            Assert.Equal(replyRunId, row.ReplyRunId);
            Assert.Equal("scripted", row.Provider);
            Assert.Equal("claude-opus-5-5", row.Model);
            Assert.Equal(ModelCallOutcome.Success, row.Outcome);
            Assert.False(row.IsEstimated);
        });
        Assert.Equal([100, 150], rows.Select(r => r.InputTokens));
    }

    [Fact]
    public async Task A_retried_call_is_one_row()
    {
        var provider = new ScriptedChatClient(_time, Steps(Fail(503)), Steps(Text("Hello"), Usage(10, 1)));
        using var pipeline = TestPipeline.Create(provider, _time, new FakeErrorClassifier());

        await _time.RunAsync(pipeline.Client.GetResponseAsync("hi", Options(), Ct));

        Assert.Equal(2, provider.Calls);
        var row = Assert.Single(pipeline.Rows.All);
        Assert.Equal(ModelCallOutcome.Success, row.Outcome);
        Assert.True(row.LatencyMs >= 250, $"Latency {row.LatencyMs} ms should include the retry wait.");
    }

    [Fact]
    public async Task A_stream_cut_off_before_usage_writes_an_estimated_cancelled_row()
    {
        var provider = new ScriptedChatClient(_time, Steps(Text("Hello there"), Text(" more"), Usage(10, 3)));
        using var pipeline = TestPipeline.Create(provider, _time);

        await foreach (var update in pipeline.Client.GetStreamingResponseAsync("hi", Options(), Ct))
        {
            // The caller stops after the first update
            break;
        }

        var row = Assert.Single(pipeline.Rows.All);
        Assert.True(row.IsEstimated);
        Assert.Equal(ModelCallOutcome.Cancelled, row.Outcome);
        // "hi": 1 token, plus 10 for the message
        Assert.Equal(11, row.InputTokens);
        Assert.Equal(TokenEstimator.FromCharacters("Hello there".Length), row.OutputTokens);
    }

    [Fact]
    public async Task A_failed_call_writes_an_estimated_error_row()
    {
        var provider = new ScriptedChatClient(_time, Steps(Fail(400)));
        using var pipeline = TestPipeline.Create(provider, _time, new FakeErrorClassifier());

        await Assert.ThrowsAsync<ModelUnavailableException>(() => pipeline.Client.GetResponseAsync("hi", Options(), Ct));

        var row = Assert.Single(pipeline.Rows.All);
        Assert.Equal(ModelCallOutcome.Error, row.Outcome);
        Assert.True(row.IsEstimated);
    }

    [Fact]
    public async Task Cost_follows_the_price_table()
    {
        var provider = new ScriptedChatClient(_time, Steps(Text("x"), Usage(1000, 500)));
        using var pipeline = TestPipeline.Create(provider, _time);

        await pipeline.Client.GetResponseAsync("hi", Options(), Ct);

        // 1000 × $4 + 500 × $20, per million tokens
        Assert.Equal(0.014m, Assert.Single(pipeline.Rows.All).CostUsd);
    }

    [Fact]
    public async Task An_unknown_model_stores_a_null_cost_and_logs_a_warning()
    {
        using var logs = new CapturingLoggerProvider();
        var provider = new ScriptedChatClient(_time, Steps(Text("x"), Usage(1000, 500)));
        using var pipeline = TestPipeline.Create(provider, _time, logger: logs);
        var options = Options();
        options.ModelId = "mystery-model";

        await pipeline.Client.GetResponseAsync("hi", options, Ct);

        Assert.Null(Assert.Single(pipeline.Rows.All).CostUsd);
        Assert.Contains(logs.Messages, m => m.Contains("No price for model mystery-model", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unknown_model_is_warned_about_once()
    {
        using var logs = new CapturingLoggerProvider();
        var provider = new ScriptedChatClient(_time, Steps(Text("x")));
        using var pipeline = TestPipeline.Create(provider, _time, logger: logs);
        var options = Options();
        options.ModelId = "mystery-model";

        await pipeline.Client.GetResponseAsync("hi", options, Ct);
        await pipeline.Client.GetResponseAsync("hi", options, Ct);

        Assert.Single(logs.Messages, m => m.Contains("No price for model mystery-model", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_row_that_cannot_be_recorded_does_not_break_the_call(bool streaming)
    {
        using var logs = new CapturingLoggerProvider();
        var provider = new ScriptedChatClient(_time, Steps(Text("Hello"), Usage(10, 1)));
        using var pipeline = TestPipeline.Create(provider, _time, logger: logs, sink: new ThrowingSink());

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
        Assert.Contains(logs.Messages, m => m.Contains("Couldn't record the Reply model call", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_call_without_a_context_is_refused()
    {
        using var pipeline = TestPipeline.Create(new ScriptedChatClient(_time, Steps(Text("x"))), _time);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.Client.GetResponseAsync("hi", new ChatOptions(), Ct));
    }

    [Fact]
    public void A_full_buffer_drops_the_row_with_a_warning_and_never_waits()
    {
        using var logs = new CapturingLoggerProvider();
        var buffer = new ModelCallBuffer(Microsoft.Extensions.Options.Options.Create(TestPipeline.Settings(o => o.Usage.QueueCapacity = 1)), new Logger<ModelCallBuffer>(new LoggerFactory([logs])));

        buffer.Add(Row());
        buffer.Add(Row());

        Assert.Equal(1, buffer.Reader.Count);
        Assert.Contains(logs.Messages, m => m.Contains("buffer is full", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Prompt_and_ticket_text_are_not_logged_at_the_default_level()
    {
        using var logs = new CapturingLoggerProvider();
        // A tool loop, an unknown model and a failing call: everything that logs
        var provider = new ScriptedChatClient(_time, _ => ToolCallWithUsage(10), Steps(Fail(400)));
        using var pipeline = TestPipeline.Create(provider, _time, new FakeErrorClassifier(), logger: logs);
        var options = Options();
        options.ModelId = "mystery-model";
        options.Tools = [AIFunctionFactory.Create((string orderNumber) => TicketText, "lookup_order_status")];

        await Assert.ThrowsAsync<ModelUnavailableException>(() =>
            pipeline.Client.GetResponseAsync([new ChatMessage(ChatRole.System, "SYSTEM-PROMPT-TEXT"), new ChatMessage(ChatRole.User, TicketText)], options, Ct));

        Assert.NotEmpty(logs.Messages);
        Assert.DoesNotContain(logs.Messages, m => m.Contains(TicketText, StringComparison.Ordinal) || m.Contains("SYSTEM-PROMPT-TEXT", StringComparison.Ordinal));
    }

    private static IChatClient? InnerOf(IChatClient client) =>
        client is DelegatingChatClient
            ? (IChatClient?)typeof(DelegatingChatClient).GetProperty("InnerClient", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)
            : null;

    private static async IAsyncEnumerable<ChatResponseUpdate> ToolCallWithUsage(long tokens)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call-1", "lookup_order_status", new Dictionary<string, object?> { ["orderNumber"] = "KO-205002" })]);
        yield return new ChatResponseUpdate { Contents = [new UsageContent(new UsageDetails { InputTokenCount = tokens, OutputTokenCount = 0 })] };
    }

    private sealed class ThrowingSink : IModelCallSink
    {
        public void Add(ModelCall row) => throw new InvalidOperationException("sink is broken");
    }

    private static ModelCall Row() => new() { Provider = "fake", Model = "fake", PromptVersion = "reply.v1", Feature = AiFeature.Reply };
}
