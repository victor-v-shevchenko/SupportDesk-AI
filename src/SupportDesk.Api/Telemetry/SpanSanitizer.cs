using System.Diagnostics;
using OpenTelemetry;
using SupportDesk.Core.Telemetry;

namespace SupportDesk.Api.Telemetry;

/// <summary>
/// Keeps exception text out of spans (PLAN.md 14). Instrumentation libraries put an exception's message in a failed span's
/// status and in an <c>exception</c> event (with the stack trace), and a provider's or database's message can echo the
/// request, which holds ticket text.
/// <list type="bullet">
/// <item>The event: while this processor lives, an <see cref="ActivityListener"/> with an <c>ExceptionRecorder</c> adds
/// placeholder <c>exception.message</c> and <c>exception.stacktrace</c> tags. <see cref="Activity.AddException(Exception, in TagList, DateTimeOffset)"/>
/// runs the recorders first and skips keys already there, so the real text is never written. The listener samples nothing, so it
/// changes no span.</item>
/// <item>The status: on every span as it ends, before the exporters, the description becomes the app's error code, or the
/// exception type, never the message.</item>
/// <item>A span of a call the caller cancelled isn't an error, whatever the library that wrote it thought.</item>
/// </list>
/// </summary>
internal sealed class SpanSanitizer : BaseProcessor<Activity>
{
    private readonly ActivityListener _listener = new()
    {
        ShouldListenTo = source => Instrumented(source.Name),
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.None,
        ExceptionRecorder = Scrub,
    };

    public SpanSanitizer()
    {
        ActivitySource.AddActivityListener(_listener);
    }

    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);

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

    protected override void Dispose(bool disposing)
    {
        // AddProcessor<T> also registers the processor in DI, so both the provider and the container dispose it;
        // detaching the listener a second time does nothing
        if (disposing)
        {
            _listener.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// The sources that record exceptions on their spans: Npgsql, and the HTTP and ASP.NET Core libraries and their instrumentation.
    /// The app's and Microsoft.Extensions.AI's sources don't record any today; they're included so a later exception event on
    /// them is scrubbed too, and since the provider already listens to them, this changes nothing else.
    /// </summary>
    private static bool Instrumented(string name) =>
        name == "Npgsql"
        || name == SupportDeskTelemetry.SourceName
        || name == SupportDeskTelemetry.AiSourceName
        || name.StartsWith("System.Net.", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
        || name.StartsWith("OpenTelemetry.Instrumentation.", StringComparison.Ordinal);

    private static void Scrub(Activity activity, Exception exception, ref TagList tags)
    {
        tags.Add("exception.message", exception.GetType().FullName);
        tags.Add("exception.stacktrace", "");
    }
}
