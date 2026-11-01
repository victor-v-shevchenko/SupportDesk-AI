namespace SupportDesk.Core.Replies;

/// <summary>
/// One event of a suggested reply's stream (PLAN.md 7). A stream is <see cref="ReplyToken"/>*, then
/// <see cref="ReplyCitations"/>, <see cref="ReplyUsage"/> and <see cref="ReplyDone"/>; or it ends with one
/// <see cref="ReplyError"/>. Each record's properties are the event's JSON payload.
/// </summary>
public abstract record ReplyEvent;

/// <summary>A piece of the reply's text, as the model streamed it.</summary>
public sealed record ReplyToken(string Text) : ReplyEvent;

/// <summary>The checked citation markers: one entry per distinct valid marker and the out-of-range numbers, both sorted by number.</summary>
public sealed record ReplyCitations(IReadOnlyList<Citation> Citations, IReadOnlyList<int> Removed) : ReplyEvent;

/// <summary>A valid marker <c>[n]</c> and the article of source <c>n</c>.</summary>
public sealed record Citation(int Marker, string ArticleSlug, string ArticleTitle);

/// <summary>The request's tokens and cost; <see cref="Estimated"/> when the provider didn't report usage.</summary>
public sealed record ReplyUsage(long InputTokens, long OutputTokens, decimal? CostUsd, bool Estimated) : ReplyEvent;

/// <summary>Why the reply stopped: one of the <see cref="ReplyErrorCodes"/>, and a message that's safe to show.</summary>
public sealed record ReplyError(string Code, string Message) : ReplyEvent;

/// <summary>The last event of a reply that finished.</summary>
public sealed record ReplyDone(Guid ReplyRunId) : ReplyEvent;

/// <summary>The <c>code</c> values of an <c>error</c> event (PLAN.md 7).</summary>
public static class ReplyErrorCodes
{
    public const string ModelUnavailable = "model_unavailable";
    public const string BudgetExceeded = "budget_exceeded";
    public const string ModelRefused = "model_refused";
    public const string Timeout = "timeout";
    public const string InternalError = "internal_error";
}
