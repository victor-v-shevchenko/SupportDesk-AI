using OpenTelemetry;
using OpenTelemetry.Logs;

namespace SupportDesk.Api.Telemetry;

/// <summary>
/// Keeps exception text out of exported logs (PLAN.md 14). The OTLP exporter writes a logged exception's type, message and
/// <c>ToString()</c> (the stack trace and every inner message), and a provider's or PostgreSQL's message can echo the request,
/// which holds ticket text. This processor runs before the exporter and replaces <see cref="LogRecord.Exception"/> with
/// attributes that name only the type, and the innermost type when the exception wraps another. The console log is a
/// different provider and keeps the full exception.
/// </summary>
internal sealed class LogSanitizer : BaseProcessor<LogRecord>
{
    public const string ExceptionType = "exception.type";
    public const string InnerExceptionType = "supportdesk.inner_exception.type";

    public override void OnEnd(LogRecord data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var exception = data.Exception;
        if (exception is null)
        {
            return;
        }

        var attributes = new List<KeyValuePair<string, object?>>(data.Attributes ?? [])
        {
            new(ExceptionType, exception.GetType().FullName),
        };
        var innermost = exception.GetBaseException();
        if (!ReferenceEquals(innermost, exception))
        {
            attributes.Add(new(InnerExceptionType, innermost.GetType().FullName));
        }

        data.Attributes = attributes;
        data.Exception = null;
    }
}
