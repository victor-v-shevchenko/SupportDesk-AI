using Microsoft.EntityFrameworkCore;
using SupportDesk.Core.AI.Usage;

namespace SupportDesk.Data.Repositories;

internal sealed class ModelCallRepository(SupportDeskDbContext db) : IModelCallRepository
{
    public async Task AddRangeAsync(IReadOnlyList<ModelCall> calls, CancellationToken cancellationToken)
    {
        db.ModelCalls.AddRange(calls);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ModelCall>> ListAsync(int? ticketId, int limit, CancellationToken cancellationToken)
    {
        var query = db.ModelCalls.AsNoTracking();
        if (ticketId is { } id)
        {
            query = query.Where(c => c.TicketId == id);
        }

        // Id breaks ties between calls that started in the same instant
        return await query.OrderByDescending(c => c.Timestamp).ThenByDescending(c => c.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<decimal> SumCostSinceAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        await db.ModelCalls.Where(c => c.Timestamp >= since).SumAsync(c => c.CostUsd, cancellationToken) ?? 0m;
}
