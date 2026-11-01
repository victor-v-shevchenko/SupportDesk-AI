namespace SupportDesk.Core.FollowUps;

public enum ProposalStatus
{
    Pending,
    Confirmed,
    Expired,
}

/// <summary>
/// A follow-up task the model proposed while writing a reply (PLAN.md 4, 10). One per (ticket, reply run): a repeated or
/// retried proposal in the same run updates it. Only the Confirm endpoint turns it into a <see cref="FollowUpTask"/>.
/// </summary>
public sealed class FollowUpProposal
{
    public const int TitleMaxLength = 120;
    public const int NotesMaxLength = 500;

    public Guid Id { get; set; }

    public int TicketId { get; set; }

    public Guid ReplyRunId { get; set; }

    public required string Title { get; set; }

    public required string Notes { get; set; }

    public DateOnly? DueDate { get; set; }

    public ProposalStatus Status { get; set; } = ProposalStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>A confirmed follow-up. Created only by the Confirm endpoint, at most once per proposal.</summary>
public sealed class FollowUpTask
{
    public int Id { get; set; }

    public int TicketId { get; set; }

    public Guid ProposalId { get; set; }

    public required string Title { get; set; }

    public required string Notes { get; set; }

    public DateOnly? DueDate { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
