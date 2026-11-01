using Microsoft.EntityFrameworkCore;
using SupportDesk.Core.FollowUps;

namespace SupportDesk.Data.Repositories;

internal sealed class FollowUpRepository(SupportDeskDbContext db, TimeProvider time) : IFollowUpRepository
{
    public async Task<FollowUpProposal> UpsertProposalAsync(NewProposal proposal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var now = TicketRepository.ToDatabasePrecision(time.GetUtcNow());
        DateOnly? dueDate = proposal.DueInDays is { } days ? DateOnly.FromDateTime(now.UtcDateTime).AddDays(days) : null;
        var pending = nameof(ProposalStatus.Pending);

        // One statement, so two calls racing in the same run can't both insert: the unique index makes the second an update.
        // A retry keeps the first call's ID and expiry, and a proposal confirmed or expired in the meantime stays as it is
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "FollowUpProposals" ("Id", "TicketId", "ReplyRunId", "Title", "Notes", "DueDate", "Status", "CreatedAt", "ExpiresAt")
            VALUES ({Guid.CreateVersion7()}, {proposal.TicketId}, {proposal.ReplyRunId}, {proposal.Title}, {proposal.Notes}, {dueDate}, {pending}, {now}, {now + FollowUpRules.ProposalLifetime})
            ON CONFLICT ("TicketId", "ReplyRunId") DO UPDATE
            SET "Title" = EXCLUDED."Title", "Notes" = EXCLUDED."Notes", "DueDate" = EXCLUDED."DueDate"
            WHERE "FollowUpProposals"."Status" = {pending}
            """,
            cancellationToken);

        return await db.FollowUpProposals.AsNoTracking()
            .SingleAsync(p => p.TicketId == proposal.TicketId && p.ReplyRunId == proposal.ReplyRunId, cancellationToken);
    }

    public async Task<ConfirmResult> ConfirmAsync(Guid proposalId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Locks the row until the commit, so two confirms at once take turns and the second finds the first one's task.
        // Not composed with more LINQ, which would put FOR UPDATE in a subquery
        var proposal = (await db.FollowUpProposals
            .FromSql($"""SELECT * FROM "FollowUpProposals" WHERE "Id" = {proposalId} FOR UPDATE""")
            .ToListAsync(cancellationToken)).SingleOrDefault();
        if (proposal is null)
        {
            return new ConfirmResult(ConfirmOutcome.NotFound, null);
        }

        if (proposal.Status == ProposalStatus.Confirmed)
        {
            var existing = await db.FollowUpTasks.AsNoTracking().SingleAsync(t => t.ProposalId == proposalId, cancellationToken);
            return new ConfirmResult(ConfirmOutcome.Confirmed, existing);
        }

        var now = TicketRepository.ToDatabasePrecision(time.GetUtcNow());
        // The background check may not have run yet, so the time decides, not only the status
        if (proposal.Status == ProposalStatus.Expired || proposal.ExpiresAt <= now)
        {
            proposal.Status = ProposalStatus.Expired;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ConfirmResult(ConfirmOutcome.Expired, null);
        }

        var task = new FollowUpTask
        {
            TicketId = proposal.TicketId,
            ProposalId = proposal.Id,
            Title = proposal.Title,
            Notes = proposal.Notes,
            DueDate = proposal.DueDate,
            CreatedAt = now,
        };
        proposal.Status = ProposalStatus.Confirmed;
        db.FollowUpTasks.Add(task);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ConfirmResult(ConfirmOutcome.Confirmed, task);
    }

    public async Task<TicketFollowUps?> ListAsync(int ticketId, CancellationToken cancellationToken)
    {
        if (!await db.Tickets.AnyAsync(t => t.Id == ticketId, cancellationToken))
        {
            return null;
        }

        var proposals = await db.FollowUpProposals.AsNoTracking()
            .Where(p => p.TicketId == ticketId)
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .ToListAsync(cancellationToken);
        var tasks = await db.FollowUpTasks.AsNoTracking()
            .Where(t => t.TicketId == ticketId)
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
            .ToListAsync(cancellationToken);
        return new TicketFollowUps(proposals, tasks);
    }

    public Task<int> ExpireAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        return db.FollowUpProposals
            .Where(p => p.Status == ProposalStatus.Pending && p.ExpiresAt <= now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.Status, ProposalStatus.Expired), cancellationToken);
    }
}
