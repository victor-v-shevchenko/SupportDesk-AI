using Microsoft.Extensions.Logging;
using SupportDesk.Core.Classification;

namespace SupportDesk.Core.Telemetry;

/// <summary>
/// The log lines that carry prompt, ticket, reply or tool text (PLAN.md 14). They are written to the console as the log levels
/// allow, but the OpenTelemetry export drops them unless <c>Telemetry:CaptureMessageContent</c> is on. A rule is on the logger's
/// category, since an event ID is only unique within one:
/// <list type="bullet">
/// <item>the app's own call: a <c>[LoggerMessage]</c> that takes such text gets a fixed event ID here, matched with its category;</item>
/// <item>a library's lines: Microsoft.Extensions.AI's <c>FunctionInvokingChatClient</c> logs tool arguments and results at
/// <see cref="LogLevel.Trace"/>, so its categories below <see cref="LogLevel.Debug"/> are content.</item>
/// </list>
/// </summary>
public static class ContentLogEvents
{
    /// <summary>The classification rejection at debug level, whose raw model output can echo the ticket.</summary>
    public const int ClassificationRejected = 4101;

    /// <summary>Microsoft.Extensions.AI's categories (<c>Microsoft.Extensions.AI.FunctionInvokingChatClient</c> and the like).</summary>
    public const string LibraryCategoryPrefix = "Microsoft.Extensions.AI";

    /// <summary>Whether a line written by the logger <paramref name="category"/> at <paramref name="level"/> with <paramref name="eventId"/> holds content.</summary>
    public static bool IsContent(string? category, LogLevel level, EventId eventId)
    {
        if (category is null)
        {
            return false;
        }

        if (eventId.Id == ClassificationRejected && category == typeof(ClassificationService).FullName)
        {
            return true;
        }

        return level < LogLevel.Debug && category.StartsWith(LibraryCategoryPrefix, StringComparison.Ordinal);
    }
}
