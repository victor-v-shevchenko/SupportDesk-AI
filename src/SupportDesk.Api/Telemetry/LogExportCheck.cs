using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;

namespace SupportDesk.Api.Telemetry;

/// <summary>
/// The startup check for the log export (PLAN.md 14): the logger providers the container resolves hold exactly one
/// <see cref="ContentLogFilteringProvider"/> and no unwrapped <see cref="OpenTelemetryLoggerProvider"/>. Registering
/// OpenTelemetry logging again after <c>WithSupportDeskLogging</c> (a later <c>AddOpenTelemetry</c>, service defaults) adds a
/// provider that isn't wrapped, which would export content events, and setting it up twice exports every record twice. The
/// registrations can't show this, since the wrapped descriptor no longer names the SDK's type, so the resolved providers are
/// checked. It has no settings: it exists for <c>ValidateOnStart</c>.
/// </summary>
internal sealed class LogExportCheck
{
    public const string Failure =
        "The log export needs exactly one OpenTelemetry logger provider, wrapped by WithSupportDeskLogging; OpenTelemetry logging was registered again or set up twice, so content events could be exported unfiltered or every record twice.";

    public static OptionsBuilder<LogExportCheck> Register(IServiceCollection services) =>
        services.AddOptions<LogExportCheck>()
            .Validate<IEnumerable<ILoggerProvider>>(
                (_, providers) =>
                {
                    var list = providers.ToList();
                    return list.Count(p => p is ContentLogFilteringProvider) == 1 && !list.Any(p => p is OpenTelemetryLoggerProvider);
                },
                Failure)
            .ValidateOnStart();
}
