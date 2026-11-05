using Microsoft.Extensions.Logging;

namespace SupportDesk.Core.Telemetry;

/// <summary>
/// The log events that carry prompt, ticket, reply or tool text (PLAN.md 14). They are written to the console as the log levels
/// allow, but the OpenTelemetry export drops them unless <c>Telemetry:CaptureMessageContent</c> is on. A log call that takes such
/// text gives its <c>[LoggerMessage]</c> a fixed event ID from here and adds it to <see cref="Ids"/>.
/// </summary>
public static class ContentLogEvents
{
    /// <summary>The classification rejection at debug level, whose raw model output can echo the ticket.</summary>
    public const int ClassificationRejected = 4101;

    private static readonly HashSet<int> Set = [ClassificationRejected];

    /// <summary>Every event ID that holds content.</summary>
    public static IReadOnlyCollection<int> Ids => Set;

    public static bool IsContent(EventId eventId) => Set.Contains(eventId.Id);
}
