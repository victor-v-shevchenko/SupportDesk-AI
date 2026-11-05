using System.Diagnostics;
using OpenTelemetry;
using SupportDesk.Core.Telemetry;

namespace SupportDesk.Api.Telemetry;

/// <summary>
/// Runs on every span as it ends, before the exporters (PLAN.md 14). Instrumentation libraries put an exception's message in
/// a failed span's status, and a provider's message can echo the request, which holds ticket text; so the status description
/// becomes the app's error code, or the exception type (never the message). A span of a call the caller cancelled isn't an error, whatever the library
/// that wrote it thought.
/// </summary>
internal sealed class SpanSanitizer : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Events.Any(e => e.Name == "exception"))
        {
            ReplaceWithoutExceptionEvent(data);
            return;
        }

        if (data.GetTagItem(SupportDeskTelemetry.Cancelled) is true)
        {
            data.SetStatus(ActivityStatusCode.Unset);
            data.SetTag("error.type", null);
        }
        else if (data.Status == ActivityStatusCode.Error)
        {
            data.SetStatus(ActivityStatusCode.Error, data.GetTagItem(SupportDeskTelemetry.ErrorCode) as string ?? data.GetTagItem("error.type") as string);
        }
    }

    /// <summary>
    /// Npgsql adds an <c>exception</c> event with the exception's message and stack trace to a failed command's span, and an
    /// event can't be removed. PostgreSQL's message can quote a value from the statement, so the span is kept out of the export
    /// and a copy without the event takes its place (same name, parent, times, tags and status; the type and code only).
    /// </summary>
    private static void ReplaceWithoutExceptionEvent(Activity original)
    {
        var type = original.Events.First(e => e.Name == "exception").Tags.FirstOrDefault(t => t.Key == "exception.type").Value as string;
        original.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;

        var parent = new ActivityContext(original.TraceId, original.ParentSpanId, ActivityTraceFlags.Recorded);
        using var copy = SupportDeskTelemetry.Source.StartActivity(
            original.OperationName, original.Kind, parent, original.TagObjects.Where(t => !t.Key.StartsWith("exception.", StringComparison.Ordinal)), startTime: original.StartTimeUtc);
        if (copy is null)
        {
            return;
        }

        copy.SetTag("error.type", type);
        copy.SetTag("db.original_source", original.Source.Name);
        copy.SetStatus(ActivityStatusCode.Error, type);
        copy.SetEndTime(original.StartTimeUtc + original.Duration);
    }
}
