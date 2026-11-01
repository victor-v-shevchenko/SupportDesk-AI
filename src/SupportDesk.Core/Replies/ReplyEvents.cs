using System.Text.Json.Serialization;
using SupportDesk.Core.FollowUps;

namespace SupportDesk.Core.Replies;

/// <summary>
/// One event of a suggested reply's stream (PLAN.md 7). A stream is <see cref="ReplyToken"/>s and <see cref="ReplyToolCall"/>s
/// in the order they happened, then <see cref="ReplyCitations"/>, <see cref="ReplyUsage"/> and <see cref="ReplyDone"/>; or
/// it ends with one <see cref="ReplyError"/>. Each record's properties are the event's JSON payload.
/// </summary>
public abstract record ReplyEvent;

/// <summary>A piece of the reply's text, as the model streamed it.</summary>
public sealed record ReplyToken(string Text) : ReplyEvent;

/// <summary>A tool the model called, sent after the tool ran (PLAN.md 7, 10). <see cref="Name"/> is the tool's name.</summary>
public abstract record ReplyToolCall(string Name) : ReplyEvent;

/// <summary><c>lookup_order_status</c>: what the model asked for and what it got back (<see cref="OrderFound"/> or <see cref="OrderNotFound"/>).</summary>
public sealed record ReplyOrderLookup(OrderLookupArguments Arguments, object Result) : ReplyToolCall(Tools.ReplyTools.LookupOrderStatus);

public sealed record OrderLookupArguments(string OrderNumber);

public sealed record OrderFound(string OrderNumber, string Status, string? Carrier, DateTimeOffset LastUpdate, string LastEvent)
{
    [JsonPropertyOrder(-1)]
    public bool Found { get; } = true;
}

public sealed record OrderNotFound
{
    public bool Found { get; }
}

/// <summary><c>propose_follow_up_task</c>: the saved proposal, which the user can confirm.</summary>
public sealed record ReplyProposal(ProposalPayload Proposal) : ReplyToolCall(Tools.ReplyTools.ProposeFollowUpTask);

/// <summary><see cref="Status"/> is <c>Pending</c> unless a retry in the same run found the proposal already confirmed or expired.</summary>
public sealed record ProposalPayload(Guid Id, string Title, string Notes, DateOnly? DueDate, ProposalStatus Status);

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
