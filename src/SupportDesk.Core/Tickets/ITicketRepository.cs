namespace SupportDesk.Core.Tickets;

public interface ITicketRepository
{
    /// <summary>Tickets newest first, optionally only those with <paramref name="status"/>.</summary>
    Task<IReadOnlyList<Ticket>> ListAsync(TicketStatus? status, CancellationToken cancellationToken);

    Task<Ticket?> FindAsync(int id, CancellationToken cancellationToken);

    Task<Ticket> CreateAsync(NewTicket ticket, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the ticket's classification and returns it as stored (times at the database's precision), or null when
    /// there's no ticket with <paramref name="id"/>.
    /// </summary>
    Task<TicketClassification?> SaveClassificationAsync(int id, TicketClassification classification, CancellationToken cancellationToken);

    /// <summary>Replaces the ticket's summary, stamped with the current time; null when there's no such ticket.</summary>
    Task<TicketSummary?> SaveSummaryAsync(int id, string text, string promptVersion, CancellationToken cancellationToken);
}
