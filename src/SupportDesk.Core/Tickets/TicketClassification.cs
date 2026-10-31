namespace SupportDesk.Core.Tickets;

/// <summary>Owned by <see cref="Ticket"/>; <see cref="ClassificationStatus.NotRun"/> until classified (step 7).</summary>
public sealed class TicketClassification
{
    public Category? Category { get; set; }

    public Priority? Priority { get; set; }

    public string? Reason { get; set; }

    public ClassificationStatus Status { get; set; } = ClassificationStatus.NotRun;

    public string? PromptVersion { get; set; }

    public DateTimeOffset? ClassifiedAt { get; set; }
}
