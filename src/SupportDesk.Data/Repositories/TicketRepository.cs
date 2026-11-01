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

    public async Task<TicketSummary?> SaveSummaryAsync(int id, string text, string promptVersion, CancellationToken cancellationToken)
    {
        var entity = await db.Tickets.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        entity.Summary = new TicketSummary
        {
            Text = text,
            PromptVersion = promptVersion,
            GeneratedAt = ToDatabasePrecision(time.GetUtcNow()),
        };
        await db.SaveChangesAsync(cancellationToken);
        return entity.Summary;
    }

    /// <summary>PostgreSQL keeps microseconds; truncating first makes the created ticket equal to the one read back later.</summary>
    internal static DateTimeOffset ToDatabasePrecision(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));

    internal static string FormatNumber(long value) => "T-" + value.ToString(CultureInfo.InvariantCulture);
}
