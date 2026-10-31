namespace SupportDesk.Core.Tickets;

/// <summary>Owned by <see cref="Ticket"/>; null until a summary is generated (step 6).</summary>
public sealed class TicketSummary
{
    public required string Text { get; set; }

    public required string PromptVersion { get; set; }

    public DateTimeOffset GeneratedAt { get; set; }
}
