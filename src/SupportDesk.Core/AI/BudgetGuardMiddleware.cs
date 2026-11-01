using Microsoft.Extensions.AI;

namespace SupportDesk.Core.AI;

/// <summary>
/// The per-call guard (PLAN.md 6.4): refuses a call whose input estimate is over <c>AI:Budget:MaxInputTokensPerCall</c>
/// before it reaches the provider, and sets <see cref="ChatOptions.MaxOutputTokens"/> from the feature's setting when
/// the caller didn't.
/// </summary>
public sealed class BudgetGuardMiddleware(IChatClient innerClient, AiBudgetSettings budget) : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = Materialize(messages);
        return base.GetResponseAsync(list, Prepare(list, options), cancellationToken);
    }

    // Checked when called, not on the first MoveNextAsync, so an oversized call fails before anything starts
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = Materialize(messages);
        return base.GetStreamingResponseAsync(list, Prepare(list, options), cancellationToken);
    }

    private ChatOptions? Prepare(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var estimate = TokenEstimator.EstimateInput(messages, options);
        if (estimate > budget.MaxInputTokensPerCall)
        {
            throw new BudgetExceededException($"The call's input is about {estimate} tokens, over the limit of {budget.MaxInputTokensPerCall}.")
            {
                Tokens = estimate,
                Limit = budget.MaxInputTokensPerCall,
            };
        }

        if (options?.MaxOutputTokens is not null
            || AiCallContext.From(options) is not { } context
            || !budget.MaxOutputTokens.TryGetValue(context.Feature, out var maxOutput))
        {
            return options;
        }

        var limited = options!.Clone();
        limited.MaxOutputTokens = maxOutput;
        return limited;
    }

    private static IReadOnlyList<ChatMessage> Materialize(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return messages as IReadOnlyList<ChatMessage> ?? [.. messages];
    }
}
