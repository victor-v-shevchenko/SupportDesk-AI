using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace SupportDesk.Core.AI;

/// <summary>
/// The request cap (PLAN.md 6.4), in two parts. This one is outermost: it starts a <see cref="RequestTally"/> for the
/// request and passes it down in <see cref="ChatOptions.AdditionalProperties"/>. <see cref="RequestCapCounterMiddleware"/>,
/// right under the tool loop, adds each call's tokens and refuses the next call once the tally is over
/// <c>AI:Budget:MaxTokensPerRequest</c>. One middleware can't do both: outside the tool loop it doesn't see the calls,
/// and inside it doesn't know where a request starts.
/// </summary>
public sealed class RequestCapMiddleware(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public const string TallyKey = "supportdesk.requestTally";

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, WithTally(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, WithTally(options), cancellationToken);

    /// <summary>A copy of the options with a new tally; the caller's options, which it may reuse, are left alone.</summary>
    private static ChatOptions WithTally(ChatOptions? options)
    {
        var withTally = options?.Clone() ?? new ChatOptions();
        withTally.AdditionalProperties = withTally.AdditionalProperties?.Clone() ?? [];
        withTally.AdditionalProperties[TallyKey] = new RequestTally();
        return withTally;
    }
}

/// <summary>Input and output tokens so far in one request. Calls in a tool loop run one after another, but adding is atomic anyway.</summary>
public sealed class RequestTally
{
    private long _tokens;

    public long Tokens => Interlocked.Read(ref _tokens);

    public void Add(long tokens) => Interlocked.Add(ref _tokens, tokens);

    public static RequestTally? From(ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(RequestCapMiddleware.TallyKey, out RequestTally? tally) == true ? tally : null;
}

/// <summary>The inner part of the request cap; see <see cref="RequestCapMiddleware"/>. A call without a tally isn't counted.</summary>
public sealed class RequestCapCounterMiddleware(IChatClient innerClient, AiBudgetSettings budget) : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var tally = RequestTally.From(options);
        ThrowIfOver(tally);

        var list = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        ChatResponse? response = null;
        try
        {
            response = await base.GetResponseAsync(list, options, cancellationToken);
            return response;
        }
        finally
        {
            // A failed call may still have used tokens; without usage, count the input estimate
            tally?.Add(CallTokens.Resolve(response?.Usage, list, options, response?.Messages.SelectMany(m => m.Contents) ?? []).Total);
        }
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var tally = RequestTally.From(options);
        ThrowIfOver(tally);

        return tally is null ? base.GetStreamingResponseAsync(messages, options, cancellationToken) : CountAsync(messages, options, tally, cancellationToken);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> CountAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options, RequestTally tally, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var counter = new StreamTokens();
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken))
            {
                counter.Observe(update);
                yield return update;
            }
        }
        finally
        {
            tally.Add(counter.Resolve(list, options).Total);
        }
    }

    private void ThrowIfOver(RequestTally? tally)
    {
        if (tally is { } t && t.Tokens > budget.MaxTokensPerRequest)
        {
            throw new BudgetExceededException($"The request has used about {t.Tokens} tokens, over the limit of {budget.MaxTokensPerRequest}.")
            {
                Tokens = t.Tokens,
                Limit = budget.MaxTokensPerRequest,
            };
        }
    }
}
