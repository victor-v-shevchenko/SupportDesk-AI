using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using SupportDesk.Core.Replies;

namespace SupportDesk.Api.Endpoints;

/// <summary>Turns reply events into SSE items (PLAN.md 7): the event name, and the record itself as the JSON payload.</summary>
public static class ReplySse
{
    public const string Token = "token";
    public const string ToolCall = "tool_call";
    public const string Citations = "citations";
    public const string Usage = "usage";
    public const string Error = "error";
    public const string Done = "done";

    /// <summary>Typed as <see cref="object"/> so each payload is serialized as its own record type, not as the empty base type.</summary>
    public static async IAsyncEnumerable<SseItem<object>> ToSse(IAsyncEnumerable<ReplyEvent> events, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);

        await foreach (var reply in events.WithCancellation(cancellationToken))
        {
            yield return new SseItem<object>(reply, EventName(reply));
        }
    }

    public static string EventName(ReplyEvent reply) => reply switch
    {
        ReplyToken => Token,
        ReplyToolCall => ToolCall,
        ReplyCitations => Citations,
        ReplyUsage => Usage,
        ReplyError => Error,
        ReplyDone => Done,
        _ => throw new ArgumentOutOfRangeException(nameof(reply), reply?.GetType().Name, "Unknown reply event."),
    };
}
