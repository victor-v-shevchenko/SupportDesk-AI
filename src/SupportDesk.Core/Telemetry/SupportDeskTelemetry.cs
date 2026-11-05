using System.Diagnostics;
using SupportDesk.Core.Replies;

namespace SupportDesk.Core.Telemetry;

/// <summary>
/// The app's own spans (PLAN.md 14): one <see cref="ActivitySource"/>, the tag names, and how a failure is recorded. Spans
/// carry IDs, counts and codes, never ticket, prompt, reply or tool text. Without a listener
/// <see cref="ActivitySource.StartActivity(string, ActivityKind)"/> returns null, so tracing that is off costs a null check.
/// </summary>
public static class SupportDeskTelemetry
{
    /// <summary>The name of the app's source, which the API subscribes to.</summary>
    public const string SourceName = "SupportDesk";

    /// <summary>
    /// Microsoft.Extensions.AI's own source (<c>chat</c>, <c>embeddings</c> and <c>execute_tool</c> spans). Named here so the
    /// pipeline, the subscription and the tagging middleware can't drift apart.
    /// </summary>
    public const string AiSourceName = "Experimental.Microsoft.Extensions.AI";

    public const string Feature = "supportdesk.feature";
    public const string PromptVersion = "supportdesk.prompt_version";
    public const string TicketId = "supportdesk.ticket_id";
    public const string ReplyRunId = "supportdesk.reply_run_id";
    public const string Outcome = "supportdesk.outcome";
    public const string ErrorCode = "supportdesk.error_code";
    public const string Cancelled = "supportdesk.cancelled";
    public const string RowsChanged = "supportdesk.rows_changed";

    // The source lives as long as the process, like a logger category
    public static readonly ActivitySource Source = new(SourceName);

    /// <summary>
    /// Records how an operation failed: status <c>Error</c> with the code the client sees (<see cref="ReplyErrors"/>) as its
    /// description, the exception's type, and never its message, which can echo the request and so the ticket. A caller's own
    /// cancellation isn't an error: the status stays <c>Unset</c> and the span says so with <see cref="Cancelled"/>.
    /// </summary>
    public static void RecordFailure(this Activity? activity, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (activity is null)
        {
            return;
        }

        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            activity.SetTag(Cancelled, true);
            return;
        }

        var code = ReplyErrors.From(exception).Code;
        activity.SetTag("error.type", exception.GetType().FullName);
        activity.SetTag(ErrorCode, code);
        activity.SetStatus(ActivityStatusCode.Error, code);
    }

    /// <summary>
    /// <see cref="RecordFailure"/> as an exception filter: always false, so the exception goes on unchanged
    /// (<c>catch (Exception e) when (activity.RecordedFailure(e, token)) { throw; }</c> never catches).
    /// </summary>
    public static bool RecordedFailure(this Activity? activity, Exception exception, CancellationToken cancellationToken)
    {
        activity.RecordFailure(exception, cancellationToken);
        return false;
    }
}
