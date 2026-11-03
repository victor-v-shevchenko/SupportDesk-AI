using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SupportDesk.Core.AI;

namespace SupportDesk.UnitTests.AI;

public class OllamaOptionsMiddlewareTests
{
    private static readonly Dictionary<AiFeature, bool> Think = new()
    {
        [AiFeature.Classification] = false,
        [AiFeature.Summary] = true,
    };

    [Fact]
    public void Turns_thinking_off_for_a_feature_set_to_false_in_a_copy()
    {
        using var middleware = new OllamaOptionsMiddleware(new FakeNothingClient(), Think);
        var options = new AiCallContext(AiFeature.Classification, "v1").ApplyTo(new ChatOptions { MaxOutputTokens = 200 });

        var shaped = middleware.Shape(options)!;

        Assert.Equal(ReasoningEffort.None, shaped.Reasoning?.Effort);
        Assert.Equal(200, shaped.MaxOutputTokens);
        // The caller's options are left as they were
        Assert.Null(options.Reasoning);
    }

    [Theory]
    [InlineData(AiFeature.Summary)]
    [InlineData(AiFeature.Reply)]
    public void Leaves_thinking_to_the_model_for_true_or_a_missing_feature(AiFeature feature)
    {
        using var middleware = new OllamaOptionsMiddleware(new FakeNothingClient(), Think);
        var options = new AiCallContext(feature, "v1").ApplyTo(new ChatOptions());

        Assert.Same(options, middleware.Shape(options));
        Assert.Null(options.Reasoning);
    }

    [Fact]
    public void Keeps_reasoning_the_caller_set_and_leaves_calls_without_context_alone()
    {
        using var middleware = new OllamaOptionsMiddleware(new FakeNothingClient(), Think);
        var explicitReasoning = new AiCallContext(AiFeature.Classification, "v1").ApplyTo(new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low } });
        var noContext = new ChatOptions();

        Assert.Equal(ReasoningEffort.Low, middleware.Shape(explicitReasoning)?.Reasoning?.Effort);
        Assert.Same(noContext, middleware.Shape(noContext));
        Assert.Null(middleware.Shape(null));
    }

    [Fact]
    public void Configuration_can_turn_thinking_back_on_for_a_feature()
    {
        using var services = AiTestServices.Build(new Dictionary<string, string?> { ["AI:Ollama:Think:Classification"] = "true" });

        var think = services.GetRequiredService<IOptions<AiOptions>>().Value.Ollama.Think;

        Assert.True(think[AiFeature.Classification]);
        Assert.False(think[AiFeature.Summary]);
    }

    private sealed class FakeNothingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
            // Nothing to release
        }
    }
}
