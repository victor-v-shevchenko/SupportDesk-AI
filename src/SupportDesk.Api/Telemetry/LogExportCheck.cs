using OpenTelemetry.Logs;

namespace SupportDesk.Api.Telemetry;

/// <summary>
/// The startup check for the log export (PLAN.md 14): the logger providers the container resolves hold exactly one
/// <see cref="ContentLogFilteringProvider"/> and no unwrapped <see cref="OpenTelemetryLoggerProvider"/>. Registering
/// OpenTelemetry logging again after <c>WithSupportDeskLogging</c> (a later <c>AddOpenTelemetry</c>, service defaults) adds a
/// provider that isn't wrapped, which would export content events, and setting it up twice exports every record twice. The
/// registrations can't show this, since the wrapped descriptor no longer names the SDK's type, so the resolved providers are
/// checked, and a failure stops the host from starting.
/// </summary>
internal sealed class LogExportCheck(IEnumerable<ILoggerProvider> providers) : IHostedService
{
    public const string Failure =
        "The log export needs exactly one OpenTelemetry logger provider, wrapped by WithSupportDeskLogging; OpenTelemetry logging was registered again or set up twice, so content events could be exported unfiltered or every record twice.";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var list = providers.ToList();
        if (list.Count(p => p is ContentLogFilteringProvider) != 1 || list.Any(p => p is OpenTelemetryLoggerProvider))
        {
            throw new InvalidOperationException(Failure);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
