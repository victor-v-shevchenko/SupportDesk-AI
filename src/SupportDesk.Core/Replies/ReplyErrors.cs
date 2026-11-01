using SupportDesk.Core.AI;

namespace SupportDesk.Core.Replies;

/// <summary>
/// Maps the failures a reply can end with (PLAN.md 6.5) to <c>error</c> events (PLAN.md 7). The messages are fixed text,
/// never the exception's message, which can echo the provider's response or the ticket.
/// </summary>
public static class ReplyErrors
{
    public static ReplyError From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            ModelUnavailableException { IsTimeout: true } => new(ReplyErrorCodes.Timeout, "The model took too long to answer. Try again."),
            ModelUnavailableException => new(ReplyErrorCodes.ModelUnavailable, "The model is unavailable right now. Try again in a moment."),
            BudgetExceededException => new(ReplyErrorCodes.BudgetExceeded, "This ticket is too long for a suggested reply."),
            ModelRefusedException => new(ReplyErrorCodes.ModelRefused, "The model declined to write a reply for this ticket."),
            _ => new(ReplyErrorCodes.InternalError, "Something went wrong while writing the reply."),
        };
    }
}
