using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;
using SupportDesk.Api.AI;
using SupportDesk.Core.AI;
using SupportDesk.Core.Classification;
using SupportDesk.Core.Replies.Tools;
using SupportDesk.Core.Tickets;
using SupportDesk.UnitTests.Replies;

namespace SupportDesk.UnitTests.AI;

/// <summary>
/// The real provider adapters behind the pipeline, against a stub HTTP handler: their errors and refusals come out typed,
/// and the requests they send carry what PLAN.md 6.7 asks for and nothing of ours that the provider doesn't expect.
/// </summary>
public class ProviderAdapterTests
{
    private const string AnthropicRefusal = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":"refusal","stop_sequence":null,"usage":{"input_tokens":12,"output_tokens":0}}
        """;

    private const string AnthropicError = """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""";

    private const string AnthropicText = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","content":[{"type":"text","text":"Hello"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":12,"output_tokens":1}}
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ChatOptions ReplyOptions => new AiCallContext(AiFeature.Reply, "reply.v1", 2, "T-1002", Guid.NewGuid()).ApplyTo(new ChatOptions());

    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(529, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    public async Task Anthropic_errors_come_out_as_ModelUnavailable(int status, bool transient)
    {
        using var handler = new StubHttpHandler((HttpStatusCode)status, AnthropicError);
        using var client = Pipeline(Anthropic(handler), new AnthropicErrorClassifier());

        var error = await Assert.ThrowsAsync<ModelUnavailableException>(() => client.GetResponseAsync("hi", ReplyOptions, Ct));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(transient, error.IsTransient);
        Assert.Single(handler.Requests); // the SDK doesn't retry by itself
    }

    [Fact]
    public async Task Anthropic_refusal_comes_out_as_ModelRefused()
    {
        using var handler = new StubHttpHandler(HttpStatusCode.OK, AnthropicRefusal);
        using var client = Pipeline(Anthropic(handler), new AnthropicErrorClassifier());

        var error = await Assert.ThrowsAsync<ModelRefusedException>(() => client.GetResponseAsync("hi", ReplyOptions, Ct));

        Assert.Equal(12, error.Usage?.InputTokenCount);
    }

    [Fact]
    public async Task Anthropic_streamed_refusal_comes_out_as_ModelRefused_after_the_text_so_far()
    {
        const string stream = """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":12,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"refusal","stop_sequence":null},"usage":{"output_tokens":2}}

            event: message_stop
            data: {"type":"message_stop"}


            """;
        using var handler = new StubHttpHandler(HttpStatusCode.OK, stream, "text/event-stream");
        using var client = Pipeline(Anthropic(handler), new AnthropicErrorClassifier());

        var text = "";
        await Assert.ThrowsAsync<ModelRefusedException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync("hi", ReplyOptions, Ct))
            {
                text += update.Text;
            }
        });

        Assert.Equal("Hel", text);
    }

    [Fact]
    public async Task Anthropic_requests_carry_the_feature_effort_and_no_sampling_options_or_context()
    {
        using var handler = new StubHttpHandler(HttpStatusCode.OK, AnthropicText);
        using var client = Pipeline(
            new AnthropicOptionsMiddleware(Anthropic(handler), new Dictionary<AiFeature, ReasoningEffort> { [AiFeature.Reply] = ReasoningEffort.Medium }),
            new AnthropicErrorClassifier());
        var options = ReplyOptions;
        options.Temperature = 0.2f;
        options.TopP = 0.9f;
        options.TopK = 40;

        await client.GetResponseAsync("hi", options, Ct);

        var body = Assert.Single(handler.Requests);
        Assert.Contains("\"effort\":\"medium\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("temperature", body, StringComparison.Ordinal);
        Assert.DoesNotContain("top_p", body, StringComparison.Ordinal);
        Assert.DoesNotContain("top_k", body, StringComparison.Ordinal);
        Assert.DoesNotContain("supportdesk", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// PLAN.md 6.7: forced tool choice returns a 400 on current models, so structured output must go through
    /// <c>output_config.format</c>. The adapter (Anthropic 13.3) moves keywords the API doesn't take, such as <c>maxLength</c>,
    /// into the description, which is one more reason the service validates the answer itself.
    /// </summary>
    [Fact]
    public async Task Anthropic_structured_output_uses_output_config_format_and_no_forced_tool()
    {
        using var handler = new StubHttpHandler(HttpStatusCode.OK, """
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","content":[{"type":"text","text":"{\"category\":\"Other\",\"priority\":\"Low\",\"reason\":\"A question.\"}"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":12,"output_tokens":20}}
            """);
        using var client = Pipeline(Anthropic(handler), new AnthropicErrorClassifier());
        var options = new AiCallContext(AiFeature.Classification, "classify.v2", 3, "T-1003").ApplyTo(new ChatOptions());

        var response = await client.GetResponseAsync<ClassificationResult>("Classify this ticket.", ClassificationJson.Options, options, cancellationToken: Ct);

        Assert.True(ClassificationValidator.Validate(response.Text).IsValid);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests));
        var request = body.RootElement;
        Assert.False(request.TryGetProperty("tool_choice", out _));
        Assert.False(request.TryGetProperty("tools", out _));
        var format = request.GetProperty("output_config").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var schema = format.GetProperty("schema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["category", "priority", "reason"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Order(StringComparer.Ordinal));
        var properties = schema.GetProperty("properties");
        Assert.Equal(Enum.GetNames<Category>(), properties.GetProperty("category").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        var reason = properties.GetProperty("reason");
        Assert.False(reason.TryGetProperty("maxLength", out _));
        Assert.Contains("maxLength: 200", reason.GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    public async Task OpenAI_errors_come_out_as_ModelUnavailable(int status, bool transient)
    {
        using var handler = new StubHttpHandler((HttpStatusCode)status, """{"error":{"message":"nope","type":"server_error"}}""");
        using var client = Pipeline(OpenAI(handler), new OpenAIErrorClassifier());

        var error = await Assert.ThrowsAsync<ModelUnavailableException>(() => client.GetResponseAsync("hi", ReplyOptions, Ct));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(transient, error.IsTransient);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OpenAI_requests_do_not_carry_the_call_context()
    {
        using var handler = new StubHttpHandler(HttpStatusCode.OK, """
            {"id":"c1","object":"chat.completion","created":1,"model":"gpt","choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1,"total_tokens":4}}
            """);
        using var client = Pipeline(OpenAI(handler), new OpenAIErrorClassifier());

        await client.GetResponseAsync("hi", ReplyOptions, Ct);

        Assert.DoesNotContain("supportdesk", Assert.Single(handler.Requests), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(404, false)]
    public async Task Ollama_errors_come_out_as_ModelUnavailable(int status, bool transient)
    {
        using var handler = new StubHttpHandler((HttpStatusCode)status, """{"error":"model 'qwen2.5:7b' not found"}""");
        using var client = Pipeline(Ollama(handler), new OllamaErrorClassifier());

        var error = await Assert.ThrowsAsync<ModelUnavailableException>(() => client.GetResponseAsync("hi", ReplyOptions, Ct));

        Assert.Equal(transient, error.IsTransient);
    }

    [Fact]
    public async Task Ollama_requests_do_not_carry_the_call_context()
    {
        using var handler = new StubHttpHandler(HttpStatusCode.OK, """
            {"model":"qwen2.5:7b","created_at":"2026-11-01T00:00:00Z","message":{"role":"assistant","content":"Hello"},"done":true,"done_reason":"stop","prompt_eval_count":3,"eval_count":1}
            """, "application/x-ndjson");
        using var client = Pipeline(Ollama(handler), new OllamaErrorClassifier());

        await client.GetResponseAsync("hi", ReplyOptions, Ct);

        Assert.DoesNotContain("supportdesk", Assert.Single(handler.Requests), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ollama_requests_carry_the_reply_tools_with_their_schemas()
    {
        using var handler = new StubHttpHandler(HttpStatusCode.OK, """
            {"model":"qwen2.5:7b","created_at":"2026-11-01T00:00:00Z","message":{"role":"assistant","content":"Hello"},"done":true,"done_reason":"stop","prompt_eval_count":3,"eval_count":1}
            """, "application/x-ndjson");
        using var client = Pipeline(Ollama(handler), new OllamaErrorClassifier());
        var options = ReplyOptions;
        options.Tools = [.. new ReplyToolFactory(new StubOrders(), new RecordingFollowUps()).Create(ReplyTestData.Ticket(), Guid.NewGuid()).Tools];

        await client.GetStreamingResponseAsync("hi", options, Ct).ToChatResponseAsync(Ct);

        var tools = JsonDocument.Parse(Assert.Single(handler.Requests)).RootElement.GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal([ReplyTools.LookupOrderStatus, ReplyTools.ProposeFollowUpTask], tools.Select(t => t.GetProperty("function").GetProperty("name").GetString()));
        var orderNumber = tools[0].GetProperty("function").GetProperty("parameters").GetProperty("properties").GetProperty("orderNumber");
        Assert.Equal("string", orderNumber.GetProperty("type").GetString());
    }

    // One attempt: these tests are about how each SDK's errors are mapped; RetryTimeoutMiddlewareTests covers retries
    private static IChatClient Pipeline(IChatClient provider, IModelErrorClassifier classifier) =>
        TestPipeline.Create(provider, classifier: classifier, configure: o => o.Retry.MaxAttempts = 1).Client;

    private static IChatClient Anthropic(StubHttpHandler handler) =>
        new AnthropicClient(new ClientOptions { ApiKey = "test-key", MaxRetries = 0, HttpClient = new HttpClient(handler, disposeHandler: false) })
            .AsIChatClient("claude-opus-5-5");

    private static IChatClient OpenAI(StubHttpHandler handler) =>
        new OpenAIClient(new ApiKeyCredential("test-key"), new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(new HttpClient(handler, disposeHandler: false)),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        }).GetChatClient("gpt-test").AsIChatClient();

    private static OllamaApiClient Ollama(StubHttpHandler handler) =>
        new OllamaApiClient(new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://ollama.test") }, "qwen2.5:7b");
}
