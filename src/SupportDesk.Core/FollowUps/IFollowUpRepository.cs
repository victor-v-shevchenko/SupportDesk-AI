namespace SupportDesk.Core.FollowUps;

/// <summary>What the proposal tool asks for; the repository stamps the times.</summary>
public sealed record NewProposal(int TicketId, Guid ReplyRunId, string Title, string Notes, int? DueInDays);

public enum ConfirmOutcome
{
    Confirmed,
    NotFound,
    Expired,
}

/// <summary>The task, for <see cref="ConfirmOutcome.Confirmed"/>: the new one, or the one an earlier confirm created.</summary>
public sealed record ConfirmResult(ConfirmOutcome Outcome, FollowUpTask? Task);

public sealed record TicketFollowUps(IReadOnlyList<FollowUpProposal> Proposals, IReadOnlyList<FollowUpTask> Tasks);

public interface IFollowUpRepository
{
    /// <summary>
    /// Saves the proposal for (ticket, reply run), or updates the title, notes and due date of the one already there, in
    /// one statement, so retries racing each other still leave one row. A proposal that's no longer pending isn't changed.
    /// Returns the stored proposal.
    /// </summary>
    Task<FollowUpProposal> UpsertProposalAsync(NewProposal proposal, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the task for a pending proposal and marks it confirmed. A confirmed proposal returns its task again; a proposal
    /// past <see cref="FollowUpProposal.ExpiresAt"/> is marked expired and nothing is created.
    /// </summary>
    Task<ConfirmResult> ConfirmAsync(Guid proposalId, CancellationToken cancellationToken);

    /// <summary>The ticket's proposals and tasks, newest first; null when there's no such ticket.</summary>
    Task<TicketFollowUps?> ListAsync(int ticketId, CancellationToken cancellationToken);

    /// <summary>Marks pending proposals past their expiry as expired and returns how many.</summary>
    Task<int> ExpireAsync(CancellationToken cancellationToken);
}

/// <summary>Fixed by PLAN.md 10.</summary>
public static class FollowUpRules
{
    public static readonly TimeSpan ProposalLifetime = TimeSpan.FromHours(24);

    public static readonly TimeSpan ExpiryCheckInterval = TimeSpan.FromMinutes(10);

    public const int MinDueInDays = 1;
    public const int MaxDueInDays = 30;
}
