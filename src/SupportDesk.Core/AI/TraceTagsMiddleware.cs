using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using SupportDesk.Core.Telemetry;

namespace SupportDesk.Core.AI;

/// <summary>
/// Sits directly under the telemetry client (PLAN.md 6.2, 14) and adds to its <c>chat</c> span what only the app knows: the
/// feature, prompt version, ticket ID and reply run ID from <see cref="AiCallContext"/> (IDs only), and, when the call fails,
/// the error code the client sees. With no <c>chat</c> span (tracing off) it passes the call straight through.
/// </summary>
public sealed class TraceTagsMiddleware(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var activity = TagChatSpan(options);
        if (activity is null)
        {
            return base.GetResponseAsync(messages, options, cancellationToken);
        }

        try
        {
            return RecordAsync(activity, base.GetResponseAsync(messages, options, cancellationToken), cancellationToken);
        }
        catch (Exception exception) when (activity.RecordedFailure(exception, cancellationToken))
        {
            throw;
        }
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var activity = TagChatSpan(options);
        if (activity is null)
        {
            return base.GetStreamingResponseAsync(messages, options, cancellationToken);
        }

        try
        {
            return RecordAsync(activity, base.GetStreamingResponseAsync(messages, options, cancellationToken), cancellationToken);
        }
        catch (Exception exception) when (activity.RecordedFailure(exception, cancellationToken))
        {
            // A budget refusal is thrown when the call is made, not on the first read (PLAN.md 6.2)
            throw;
        }
    }

    /// <summary>The span the telemetry client opened for this call, tagged; null when there is none or nobody is listening.</summary>
    private static Activity? TagChatSpan(ChatOptions? options)
    {
        // The telemetry client has just started its span, which makes it current; the tool loop's own spans use another name
        var activity = Activity.Current;
        if (activity is not { IsAllDataRequested: true }
            || activity.Source.Name != SupportDeskTelemetry.AiSourceName
            || !activity.OperationName.StartsWith("chat", StringComparison.Ordinal))
        {
            return null;
        }

        if (AiCallContext.From(options) is { } context)
        {
            activity.SetTag(SupportDeskTelemetry.Feature, context.Feature.ToString());
            activity.SetTag(SupportDeskTelemetry.PromptVersion, context.PromptVersion);
            if (context.TicketId is { } ticketId)
            {
                activity.SetTag(SupportDeskTelemetry.TicketId, ticketId);
            }

            if (context.ReplyRunId is { } replyRunId)
            {
                activity.SetTag(SupportDeskTelemetry.ReplyRunId, replyRunId.ToString());
            }
        }

        return activity;
    }

    private static async Task<ChatResponse> RecordAsync(Activity activity, Task<ChatResponse> call, CancellationToken cancellationToken)
    {
        try
        {
            return await call;
        }
        catch (Exception exception) when (activity.RecordedFailure(exception, cancellationToken))
        {
            throw;
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> RecordAsync(
        Activity activity, IAsyncEnumerable<ChatResponseUpdate> updates, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var ended = false;
        var enumerator = updates.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        ended = true;
                        yield break;
                    }
                }
                catch (Exception exception)
                {
                    ended = true;
                    activity.RecordFailure(exception, cancellationToken);
                    throw;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
            if (!ended)
            {
                // The caller stopped reading: the same "cancelled" the usage row reports (PLAN.md 6.6)
                activity.SetTag(SupportDeskTelemetry.Cancelled, true);
            }
        }
    }
}
