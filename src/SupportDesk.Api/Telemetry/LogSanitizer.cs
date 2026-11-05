using OpenTelemetry;
using OpenTelemetry.Logs;

namespace SupportDesk.Api.Telemetry;

/// <summary>
/// Keeps exception text out of exported logs (PLAN.md 14). The OTLP exporter writes a logged exception's type, message and
/// <c>ToString()</c> (the stack trace and every inner message), and a provider's or PostgreSQL's message can echo the request,
/// which holds ticket text. This processor runs before the exporter and replaces <see cref="LogRecord.Exception"/> with
/// attributes that name only the type, and the innermost type when the exception wraps another. An exception given as a
/// template value (EF Core's <c>{error}</c>) becomes its type name, and a record that had an exception either way is exported
/// with its template as the body. A <c>QueryString</c> attribute (ASP.NET Core's request lines) becomes <c>?Redacted</c>, in the
/// attribute and the formatted message. The console log is a different provider and keeps everything.
/// </summary>
internal sealed class LogSanitizer : BaseProcessor<LogRecord>
{
    public const string ExceptionType = "exception.type";
    public const string InnerExceptionType = "supportdesk.inner_exception.type";
    public const string QueryStringKey = "QueryString";
    public const string RedactedQuery = "?Redacted";

    public override void OnEnd(LogRecord data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var hadException = data.Exception is not null;
        var attributes = new List<KeyValuePair<string, object?>>(data.Attributes?.Count ?? 0);
        string? redactedQuery = null;
        foreach (var attribute in data.Attributes ?? [])
        {
            switch (attribute.Value)
            {
                // EF Core's SaveChangesFailed and QueryIterationFailed pass the exception as a template value ({error}), so
                // its ToString() is in the attribute and in the formatted message
                case Exception argument:
                    hadException = true;
                    attributes.Add(new(attribute.Key, argument.GetType().FullName));
                    break;
                // The hosting "Request starting" and "Request finished" lines carry the raw query string, which holds user text
                case string query when attribute.Key == QueryStringKey && query.Length > 0:
                    redactedQuery = query;
                    attributes.Add(new(attribute.Key, RedactedQuery));
                    break;
                default:
                    attributes.Add(attribute);
                    break;
            }
        }

        var exception = data.Exception;
        if (exception is not null)
        {
            attributes.Add(new(ExceptionType, exception.GetType().FullName));
            var innermost = exception.GetBaseException();
            if (!ReferenceEquals(innermost, exception))
            {
                attributes.Add(new(InnerExceptionType, innermost.GetType().FullName));
            }

            data.Exception = null;
        }

        if (hadException)
        {
            // The formatted message could hold exception.ToString(); the body is then the template
            data.FormattedMessage = null;
        }
        else if (redactedQuery is not null && data.FormattedMessage is { } message)
        {
            data.FormattedMessage = message.Replace(redactedQuery, RedactedQuery, StringComparison.Ordinal);
        }

        if (hadException || redactedQuery is not null)
        {
            data.Attributes = attributes;
        }
    }
}
