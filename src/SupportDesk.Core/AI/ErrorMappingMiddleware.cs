using System.Net.Http;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace SupportDesk.Core.AI;

/// <summary>
/// Innermost, right above the provider: whatever the provider throws comes out as <see cref="ModelUnavailableException"/>,
/// and a refusal (<see cref="ChatFinishReason.ContentFilter"/>, which is how the adapters report Anthropic's
/// <c>stop_reason = "refusal"</c>) as <see cref="ModelRefusedException"/>. The caller's own cancellation stays an
/// <see cref="OperationCanceledException"/> (PLAN.md 6.5).
/// </summary>
public sealed class ErrorMappingMiddleware(IChatClient innerClient, IModelErrorClassifier? classifier) : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception exception) when (Map(exception, cancellationToken) is { } mapped)
        {
            throw mapped;
        }

        return response.FinishReason == ChatFinishReason.ContentFilter
            ? throw new ModelRefusedException { Usage = response.Usage }
            : response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IAsyncEnumerator<ChatResponseUpdate> updates;
        try
        {
            updates = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception exception) when (Map(exception, cancellationToken) is { } mapped)
        {
            throw mapped;
        }

        // A refusal is reported on an update near the end; the updates after it can still carry usage, so they're passed on
        // and the exception comes when the stream ends
        var refused = false;
        await using (updates)
        {
            while (true)
            {
                try
                {
                    if (!await updates.MoveNextAsync())
                    {
                        break;
                    }
                }
                catch (Exception exception) when (Map(exception, cancellationToken) is { } mapped)
                {
                    throw mapped;
                }

                refused |= updates.Current.FinishReason == ChatFinishReason.ContentFilter;
                yield return updates.Current;
            }
        }

        if (refused)
        {
            throw new ModelRefusedException();
        }
    }

    /// <summary>The exception to throw instead of <paramref name="exception"/>, or null to let it through unchanged.</summary>
    private ModelUnavailableException? Map(Exception exception, CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case ModelUnavailableException or ModelRefusedException:
                return null;
            case OperationCanceledException when cancellationToken.IsCancellationRequested:
                return null;
            case OperationCanceledException:
                // Not the caller's token, so the SDK or HttpClient gave up waiting
                return new ModelUnavailableException("The model provider timed out.", exception, statusCode: null, isTransient: true);
        }

        var error = classifier?.Classify(exception) ?? ClassifyCommon(exception);
        if (error is null)
        {
            return new ModelUnavailableException("The model call failed.", exception);
        }

        var message = error.StatusCode is { } status
            ? $"The model provider returned {status}."
            : "The model provider couldn't be reached.";
        return new ModelUnavailableException(message, exception, error.StatusCode, error.IsTransient);
    }

    /// <summary><see cref="HttpRequestException"/>, which Ollama and the fake provider throw directly.</summary>
    private static ModelError? ClassifyCommon(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } status } => ModelError.FromStatus((int)status),
        HttpRequestException => ModelError.Connection,
        _ => null,
    };
}
