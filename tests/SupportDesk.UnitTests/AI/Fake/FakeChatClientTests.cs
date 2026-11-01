using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Fake;

namespace SupportDesk.UnitTests.AI.Fake;

/// <summary>Each scenario in <c>tests/fixtures/fake-scenarios.json</c>, played through the shared pipeline on a fake clock.</summary>
public class FakeChatClientTests
{
    private static readonly string ScenariosPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "fake-scenarios.json");

    private readonly FakeTimeProvider _time = new();
    private readonly FakeScenarioSet _scenarios = FakeScenarioSet.Load(ScenariosPath);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_committed_file_has_every_named_scenario_on_its_own_ticket()
    {
        string[] expected = ["default", "tool-call-order", "tool-call-proposal", "error-before-first-item", "error-after-first-item", "stall", "stall-after-first-item", "invalid-json", "refusal"];

        Assert.Equal(expected.Order(StringComparer.Ordinal), _scenarios.Scenarios.Select(s => s.Name).Order(StringComparer.Ordinal));
        Assert.All(_scenarios.Scenarios.Where(s => s.Name != "default"), s => Assert.Equal(s.Name, _scenarios.ScenarioFor(s.TicketNumber).Name));
    }

    [Fact]
    public async Task Default_reply_streams_text_in_chunks_over_time_with_a_citation_and_usage_at_the_end()
    {
        using var client = Pipeline();

        var updates = await PlayAsync(client, Options(AiFeature.Reply, "T-1001"));

        var texts = updates.Where(u => u.Update.Text.Length > 0).ToList();
        Assert.True(texts.Count > 10, $"Only {texts.Count} chunks.");
        Assert.Contains("[1]", string.Concat(texts.Select(t => t.Update.Text)), StringComparison.Ordinal);
        // Each chunk arrives later than the one before it, as a real stream does
        Assert.All(texts.Zip(texts.Skip(1)), pair => Assert.True(pair.Second.At > pair.First.At));
        var last = updates[^1].Update;
        Assert.Equal(ChatFinishReason.Stop, last.FinishReason);
        Assert.Equal(48, Assert.Single(last.Contents.OfType<UsageContent>()).Details.OutputTokenCount);
    }

    [Fact]
    public async Task Default_classification_is_valid_json_and_the_summary_is_text()
    {
        using var client = Pipeline();

        var classification = await PlayResponseAsync(client, Options(AiFeature.Classification, "T-1001"));
        var summary = await PlayResponseAsync(client, Options(AiFeature.Summary, "T-1001"));

        using var json = JsonDocument.Parse(classification.Text);
        Assert.Equal("Other", json.RootElement.GetProperty("category").GetString());
        Assert.False(string.IsNullOrWhiteSpace(summary.Text));
    }

    [Fact]
    public async Task Tool_call_order_calls_the_tool_then_answers_with_its_result()
    {
        using var client = Pipeline();
        var calls = new List<string>();
        var tool = AIFunctionFactory.Create(
            (string orderNumber) =>
            {
                calls.Add(orderNumber);
                return new { status = "Delayed", carrier = "FedEx" };
            },
            "lookup_order_status");
        var options = Options(AiFeature.Reply, "T-1002");
        options.Tools = [tool];

        var updates = await PlayAsync(client, options);

        Assert.Equal(["KO-205002"], calls);
        var text = string.Concat(updates.Select(u => u.Update.Text));
        Assert.Contains("KO-205002", text, StringComparison.Ordinal);
        Assert.Contains("delayed", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_call_proposal_sends_text_then_the_proposal_arguments()
    {
        using var client = Pipeline();
        Dictionary<string, object?>? arguments = null;
        var tool = AIFunctionFactory.Create(
            (string title, string notes, int dueInDays) =>
            {
                arguments = new() { ["title"] = title, ["notes"] = notes, ["dueInDays"] = dueInDays };
                return new { status = "awaiting_user_confirmation", proposalId = Guid.Empty };
            },
            "propose_follow_up_task");
        var options = Options(AiFeature.Reply, "T-1008");
        options.Tools = [tool];

        var updates = await PlayAsync(client, options);

        Assert.NotNull(arguments);
        Assert.Equal(2, arguments["dueInDays"]);
        var call = updates.Select(u => u.Update).SelectMany(u => u.Contents).OfType<FunctionCallContent>().Single();
        Assert.Equal("propose_follow_up_task", call.Name);
        // Text before the tool call reaches the caller first
        var firstText = updates.FindIndex(u => u.Update.Text.Length > 0);
        var callIndex = updates.FindIndex(u => u.Update.Contents.OfType<FunctionCallContent>().Any());
        Assert.True(firstText < callIndex);
    }

    [Theory]
    [InlineData(AiFeature.Reply)]
    [InlineData(AiFeature.Summary)]
    [InlineData(AiFeature.Classification)]
    public async Task Error_before_first_item_fails_with_nothing_sent(AiFeature feature)
    {
        using var client = Pipeline();
        var received = new List<ChatResponseUpdate>();

        var error = await Assert.ThrowsAsync<ModelUnavailableException>(() => PlayAsync(client, Options(feature, "T-1021"), received));

        Assert.Empty(received);
        Assert.Equal(503, error.StatusCode);
        Assert.True(error.IsTransient);
    }

    [Fact]
    public async Task Error_after_first_item_fails_after_some_text()
    {
        using var client = Pipeline();
        var received = new List<ChatResponseUpdate>();

        var error = await Assert.ThrowsAsync<ModelUnavailableException>(() => PlayAsync(client, Options(AiFeature.Reply, "T-1022"), received));

        Assert.NotEmpty(received);
        Assert.StartsWith("Thanks for your question", string.Concat(received.Select(u => u.Text)), StringComparison.Ordinal);
        Assert.Equal(529, error.StatusCode);
    }

    [Fact]
    public async Task Error_after_first_item_on_another_feature_plays_the_default()
    {
        using var client = Pipeline();

        var summary = await PlayResponseAsync(client, Options(AiFeature.Summary, "T-1022"));

        Assert.False(string.IsNullOrWhiteSpace(summary.Text));
    }

    [Fact]
    public async Task Stall_sends_nothing_for_thirty_seconds_then_the_reply()
    {
        using var client = new FakeChatClient(_scenarios, _time);
        await using var stream = client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], Options(AiFeature.Reply, "T-1023"), Ct).GetAsyncEnumerator(Ct);

        var first = stream.MoveNextAsync().AsTask();
        _time.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(50, Ct);
        Assert.False(first.IsCompleted);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await AdvanceUntilDoneAsync(first));
        Assert.Equal("Thanks", stream.Current.Text);
    }

    [Fact]
    public async Task Stall_after_first_item_sends_some_text_then_nothing_for_thirty_seconds()
    {
        using var client = new FakeChatClient(_scenarios, _time);
        await using var stream = client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], Options(AiFeature.Reply, "T-1019"), Ct).GetAsyncEnumerator(Ct);

        var text = "";
        while (!text.EndsWith("error. ", StringComparison.Ordinal))
        {
            Assert.True(await AdvanceUntilDoneAsync(stream.MoveNextAsync().AsTask()));
            text += stream.Current.Text;
        }

        var next = stream.MoveNextAsync().AsTask();
        _time.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(50, Ct);
        Assert.False(next.IsCompleted);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await AdvanceUntilDoneAsync(next));
        Assert.Equal("Please", stream.Current.Text);
    }

    [Fact]
    public async Task A_stall_ends_at_once_when_the_caller_cancels()
    {
        using var client = Pipeline();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var stream = client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], Options(AiFeature.Reply, "T-1023"), cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        var first = stream.MoveNextAsync().AsTask();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task Invalid_json_classification_is_not_json_and_the_retry_does_not_match_the_schema()
    {
        using var client = Pipeline();
        var options = Options(AiFeature.Classification, "T-1024");

        var first = await PlayResponseAsync(client, options);
        var retry = await PlayResponseAsync(client, options, history: [new ChatMessage(ChatRole.Assistant, first.Text), new ChatMessage(ChatRole.User, "That wasn't valid JSON.")]);

        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(first.Text));
        using var json = JsonDocument.Parse(retry.Text);
        Assert.Equal("Privacy", json.RootElement.GetProperty("category").GetString());
        Assert.False(json.RootElement.TryGetProperty("reason", out _));
    }

    [Theory]
    [InlineData(AiFeature.Reply)]
    [InlineData(AiFeature.Classification)]
    public async Task Refusal_comes_out_as_ModelRefused(AiFeature feature)
    {
        using var client = Pipeline();

        await Assert.ThrowsAsync<ModelRefusedException>(() => PlayAsync(client, Options(feature, "T-1025")));
        var error = await Assert.ThrowsAsync<ModelRefusedException>(() => PlayResponseAsync(client, Options(feature, "T-1025")));
        Assert.Equal(800, error.Usage?.InputTokenCount);
    }

    [Fact]
    public async Task Calls_without_a_feature_fail_clearly_since_the_default_scripts_each_feature()
    {
        using var client = new FakeChatClient(_scenarios, _time);

        var error = await Record.ExceptionAsync(() => PlayResponseAsync(client, new ChatOptions()));

        // The default has turns per feature only, so a call that names no feature is a test bug, reported clearly
        Assert.IsType<InvalidOperationException>(error);
    }

    [Theory]
    [InlineData("""{ "scenarios": [] }""", "there is no 'default' scenario")]
    [InlineData("""{ "scenarios": [ { "name": "default", "ticketNumber": null, "turns": { "*": [ { "steps": [ { "type": "sing" } ] } ] } } ] }""", "")]
    [InlineData("""{ "scenarios": [ { "name": "default", "ticketNumber": null, "turns": { "*": [ { "steps": [ { "type": "error", "statusCode": 200 } ] } ] } } ] }""", "statusCode must be 400–599")]
    [InlineData("""{ "scenarios": [ { "name": "default", "ticketNumber": null, "turns": { "Replies": [ { "steps": [] } ] } } ] }""", "unknown feature")]
    [InlineData("""{ "scenarios": [ { "name": "default", "ticketNumber": null, "turns": { "*": [ { "steps": [], "delay": 1 } ] } } ] }""", "")]
    [InlineData("""{ "scenarios": [ { "name": "default", "ticketNumber": null, "turns": { "*": [ { "steps": [] } ] } }, { "name": "x", "ticketNumber": null, "turns": { "*": [ { "steps": [] } ] } } ] }""", "scenario 'x' has no ticket number")]
    public void A_broken_scenario_file_is_rejected(string json, string expected)
    {
        var error = Record.Exception(() => FakeScenarioSet.Parse(json));

        Assert.True(error is InvalidDataException or JsonException, error?.ToString());
        Assert.Contains(expected, error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_test_can_supply_its_own_scenarios()
    {
        var scenarios = FakeScenarioSet.Parse("""
            { "scenarios": [
              { "name": "default", "ticketNumber": null, "turns": { "*": [ { "steps": [ { "type": "text", "text": "plain", "delayMs": 0 } ] } ] } },
              { "name": "mine", "ticketNumber": "T-5000", "turns": { "Summary": [ { "steps": [ { "type": "text", "text": "custom", "chunkSize": 100, "delayMs": 0 }, { "type": "finish", "reason": "length" } ] } ] } }
            ] }
            """);
        using var pipeline = TestPipeline.Create(new FakeChatClient(scenarios, _time), _time, new FakeErrorClassifier());
        var client = pipeline.Client;

        var response = await client.GetResponseAsync("hi", Options(AiFeature.Summary, "T-5000"), Ct);

        Assert.Equal("custom", response.Text);
        Assert.Equal(ChatFinishReason.Length, response.FinishReason);
    }

    private IChatClient Pipeline() => TestPipeline.Create(new FakeChatClient(_scenarios, _time), _time, new FakeErrorClassifier()).Client;

    private static ChatOptions Options(AiFeature feature, string ticketNumber) =>
        new AiCallContext(feature, "test.v1", TicketId: 1, TicketNumber: ticketNumber).ApplyTo(new ChatOptions());

    /// <summary>Streams the call to the end, moving the fake clock on while it waits, and notes when each update arrived.</summary>
    private async Task<List<(TimeSpan At, ChatResponseUpdate Update)>> PlayAsync(IChatClient client, ChatOptions options, List<ChatResponseUpdate>? received = null)
    {
        var start = _time.GetUtcNow();
        var updates = new List<(TimeSpan, ChatResponseUpdate)>();
        await using var stream = client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, Ct).GetAsyncEnumerator(Ct);
        while (await AdvanceUntilDoneAsync(stream.MoveNextAsync().AsTask()))
        {
            updates.Add((_time.GetUtcNow() - start, stream.Current));
            received?.Add(stream.Current);
        }

        return updates;
    }

    private Task<ChatResponse> PlayResponseAsync(IChatClient client, ChatOptions options, IEnumerable<ChatMessage>? history = null) =>
        AdvanceUntilDoneAsync(client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi"), .. history ?? []], options, Ct));

    private async Task<T> AdvanceUntilDoneAsync<T>(Task<T> task)
    {
        while (!task.IsCompleted)
        {
            _time.Advance(TimeSpan.FromMilliseconds(10));
            await Task.WhenAny(task, Task.Delay(1, Ct));
        }

        return await task;
    }
}
