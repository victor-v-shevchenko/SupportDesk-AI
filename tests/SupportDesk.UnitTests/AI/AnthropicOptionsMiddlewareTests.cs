using Microsoft.Extensions.AI;
using SupportDesk.Core.AI;

namespace SupportDesk.UnitTests.AI;

public class AnthropicOptionsMiddlewareTests
{
    private static readonly Dictionary<AiFeature, ReasoningEffort> Effort = new()
    {
        [AiFeature.Reply] = ReasoningEffort.Medium,
        [AiFeature.Summary] = ReasoningEffort.Low,
    };

    [Theory]
    [InlineData(AiFeature.Reply, ReasoningEffort.Medium)]
    [InlineData(AiFeature.Summary, ReasoningEffort.Low)]
    public void Sets_the_feature_effort_and_drops_sampling_options(AiFeature feature, ReasoningEffort expected)
    {
        using var middleware = new AnthropicOptionsMiddleware(new FakeNothingClient(), Effort);
        var options = new AiCallContext(feature, "v1").ApplyTo(new ChatOptions { Temperature = 0.3f, TopP = 0.9f, TopK = 5 });

        var shaped = middleware.Shape(options)!;

        Assert.Equal(expected, shaped.Reasoning?.Effort);
        Assert.Null(shaped.Temperature);
        Assert.Null(shaped.TopP);
        Assert.Null(shaped.TopK);
        // The caller's options are left as they were
        Assert.Equal(0.3f, options.Temperature);
        Assert.Null(options.Reasoning);
    }

    [Fact]
    public void Keeps_reasoning_the_caller_set_and_leaves_features_without_effort_alone()
    {
        using var middleware = new AnthropicOptionsMiddleware(new FakeNothingClient(), Effort);
        var explicitReasoning = new AiCallContext(AiFeature.Reply, "v1").ApplyTo(new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } });
        var noEffort = new AiCallContext(AiFeature.Classification, "v1").ApplyTo(new ChatOptions());

        Assert.Equal(ReasoningEffort.High, middleware.Shape(explicitReasoning)?.Reasoning?.Effort);
        Assert.Null(middleware.Shape(noEffort)?.Reasoning);
        Assert.Null(middleware.Shape(null));
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
