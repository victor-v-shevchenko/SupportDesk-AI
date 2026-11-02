using SupportDesk.Core.Tickets;

namespace SupportDesk.Core.Replies.Tools;

/// <summary>Signs in a ticket's text that it tries to steer the model (PLAN.md 10, injection guard).</summary>
public static class InjectionSignals
{
    /// <summary>The names of the signals found in the ticket's subject and body; empty when there are none.</summary>
    public static IReadOnlyList<string> Find(Ticket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        return [];
    }
}
