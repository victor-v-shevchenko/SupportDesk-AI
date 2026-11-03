using Microsoft.Extensions.AI;

namespace SupportDesk.Core.AI;

/// <summary>
/// Turns a thinking model's thinking off for the features <c>AI:Ollama:Think</c> sets to <c>false</c> (PLAN.md 6.1, 6.4):
/// Ollama counts thinking against <c>num_predict</c>, so a model such as <c>qwen3:8b</c> can use a 200-token budget up
/// before it answers. OllamaSharp sends <see cref="ReasoningEffort.None"/> as <c>"think": false</c>, which models
/// without thinking accept. Nothing is sent for <c>true</c>: <c>"think": true</c> is a 400 on those models, and a
/// thinking model thinks by default. A <see cref="ChatOptions.Reasoning"/> the caller set is kept (no feature sets one):
/// an effort other than <c>None</c> would send <c>"think"</c> as a level, which models without thinking reject. Only
/// registered when the chat provider is Ollama.
/// </summary>
public sealed class OllamaOptionsMiddleware(IChatClient innerClient, IReadOnlyDictionary<AiFeature, bool> think)
    : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, Shape(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, Shape(options), cancellationToken);

    /// <summary>A copy of <paramref name="options"/> with thinking off where the feature asks for it; otherwise the caller's instance.</summary>
    public ChatOptions? Shape(ChatOptions? options)
    {
        if (options is null
            || options.Reasoning is not null
            || AiCallContext.From(options) is not { } context
            || !think.TryGetValue(context.Feature, out var on)
            || on)
        {
            return options;
        }

        var shaped = options.Clone();
        shaped.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None };
        return shaped;
    }
}
