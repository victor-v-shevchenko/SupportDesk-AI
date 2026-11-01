using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Core.Tickets;

namespace SupportDesk.Data.Repositories;

internal sealed class TicketRepository(SupportDeskDbContext db, TimeProvider time) : ITicketRepository
{
    public async Task<IReadOnlyList<Ticket>> ListAsync(TicketStatus? status, CancellationToken cancellationToken)
    {
        var query = db.Tickets.AsNoTracking();
        if (status is { } wanted)
        {
            query = query.Where(t => t.Status == wanted);
        }

        // Id breaks ties between tickets created in the same instant
        return await query.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).ToListAsync(cancellationToken);
    }

    public Task<Ticket?> FindAsync(int id, CancellationToken cancellationToken) =>
        db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public async Task<Ticket> CreateAsync(NewTicket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        var next = await db.Database
            .SqlQuery<long>($"SELECT nextval('\"ticket_numbers\"') AS \"Value\"")
            .SingleAsync(cancellationToken);

        var entity = new Ticket
        {
            Number = FormatNumber(next),
            Subject = ticket.Subject,
            Body = ticket.Body,
            CustomerName = ticket.CustomerName,
            OrderNumber = ticket.OrderNumber,
            Status = TicketStatus.Open,
            CreatedAt = ToDatabasePrecision(time.GetUtcNow()),
        };
        db.Tickets.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task<TicketClassification?> SaveClassificationAsync(int id, TicketClassification classification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(classification);

        DateTimeOffset? classifiedAt = classification.ClassifiedAt is { } at ? ToDatabasePrecision(at) : null;

        // One UPDATE of the classification columns, so it can't overwrite a change another request made to the rest of the ticket
        var updated = await db.Tickets
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(t => t.Classification.Status, classification.Status)
                    .SetProperty(t => t.Classification.Category, classification.Category)
                    .SetProperty(t => t.Classification.Priority, classification.Priority)
                    .SetProperty(t => t.Classification.Reason, classification.Reason)
                    .SetProperty(t => t.Classification.PromptVersion, classification.PromptVersion)
                    .SetProperty(t => t.Classification.ClassifiedAt, classifiedAt),
                cancellationToken);
        return updated == 0
            ? null
            : new TicketClassification
            {
                Status = classification.Status,
                Category = classification.Category,
                Priority = classification.Priority,
                Reason = classification.Reason,
                PromptVersion = classification.PromptVersion,
                ClassifiedAt = classifiedAt,
            };
    }

    /// <summary>PostgreSQL keeps microseconds; truncating first makes the created ticket equal to the one read back later.</summary>
    internal static DateTimeOffset ToDatabasePrecision(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));

    internal static string FormatNumber(long value) => "T-" + value.ToString(CultureInfo.InvariantCulture);
}
