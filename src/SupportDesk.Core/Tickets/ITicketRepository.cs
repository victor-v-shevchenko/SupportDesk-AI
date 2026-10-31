namespace SupportDesk.Core.Tickets;

public interface ITicketRepository
{
    /// <summary>Tickets newest first, optionally only those with <paramref name="status"/>.</summary>
    Task<IReadOnlyList<Ticket>> ListAsync(TicketStatus? status, CancellationToken cancellationToken);

    Task<Ticket?> FindAsync(int id, CancellationToken cancellationToken);

    Task<Ticket> CreateAsync(NewTicket ticket, CancellationToken cancellationToken);
}
