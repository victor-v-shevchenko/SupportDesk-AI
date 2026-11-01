using Microsoft.Extensions.AI;

namespace SupportDesk.Core.AI;

/// <summary>
/// Shapes options for Anthropic models (PLAN.md 6.7): drops <c>Temperature</c>, <c>TopP</c> and <c>TopK</c>, which newer API
/// versions reject, and sets the feature's effort. The adapter sends <see cref="ChatOptions.Reasoning"/> as
/// <c>output_config.effort</c> with adaptive thinking. Only registered when the chat provider is Anthropic.
/// </summary>
public sealed class AnthropicOptionsMiddleware(IChatClient innerClient, IReadOnlyDictionary<AiFeature, ReasoningEffort> effort)
    : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, Shape(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, Shape(options), cancellationToken);

    /// <summary>A copy of <paramref name="options"/> with the changes; the caller's instance is left alone.</summary>
    public ChatOptions? Shape(ChatOptions? options)
    {
        var featureEffort = AiCallContext.From(options) is { } context && effort.TryGetValue(context.Feature, out var value) ? value : (ReasoningEffort?)null;
        if (options is null && featureEffort is null)
        {
            return null;
        }

        var shaped = options?.Clone() ?? new ChatOptions();
        shaped.Temperature = null;
        shaped.TopP = null;
        shaped.TopK = null;
        if (shaped.Reasoning is null && featureEffort is { } e)
        {
            shaped.Reasoning = new ReasoningOptions { Effort = e };
        }

        return shaped;
    }
}
