using Microsoft.Extensions.Logging;
using SupportDesk.Core.Telemetry;

namespace SupportDesk.Api.Telemetry;

/// <summary>
/// Wraps the OpenTelemetry logger provider so a log line that holds content (<see cref="ContentLogEvents"/>: matched on the
/// category as well as the event ID and level, which is why it sits here, where the category is known) never reaches
/// the export unless <c>Telemetry:CaptureMessageContent</c> is on (PLAN.md 14). A log processor can rewrite a record but not
/// drop it, and the exporter can't be wrapped (it takes its resource from the provider that owns it), so the drop happens here,
/// before the record is made. The alias is the inner provider's, so <c>Logging:OpenTelemetry:LogLevel</c> still applies.
/// </summary>
[ProviderAlias("OpenTelemetry")]
internal sealed class ContentLogFilteringProvider(ILoggerProvider inner, Func<bool> captureContent) : ILoggerProvider, ISupportExternalScope
{
    public ILogger CreateLogger(string categoryName) => new FilteringLogger(inner.CreateLogger(categoryName), categoryName, captureContent);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        if (inner is ISupportExternalScope scoped)
        {
            scoped.SetScopeProvider(scopeProvider);
        }
    }

    public void Dispose() => inner.Dispose();

    private sealed class FilteringLogger(ILogger inner, string category, Func<bool> captureContent) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (ContentLogEvents.IsContent(category, logLevel, eventId) && !captureContent())
            {
                return;
            }

            inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
