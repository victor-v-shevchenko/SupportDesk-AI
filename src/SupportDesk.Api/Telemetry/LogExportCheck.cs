namespace SupportDesk.Api.Telemetry;

/// <summary>
/// The startup check for the log export (PLAN.md 14), as an options type with no settings, so <c>ValidateOnStart</c> runs it
/// (<c>TelemetryServiceCollectionExtensions.AddLogExportCheck</c>): the logger providers the container resolves hold exactly
/// one <see cref="ContentLogFilteringProvider"/> and no unwrapped <c>OpenTelemetryLoggerProvider</c>. Registering
/// OpenTelemetry logging again after <c>WithSupportDeskLogging</c> (a later <c>AddOpenTelemetry</c>, service defaults) adds a
/// provider that isn't wrapped, which would export content events, and setting it up twice exports every record twice. The
/// registrations can't show this, since the wrapped descriptor no longer names the SDK's type, so the resolved providers are
/// checked.
/// </summary>
internal sealed class LogExportCheck;
