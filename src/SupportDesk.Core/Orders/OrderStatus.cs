namespace SupportDesk.Core.Orders;

/// <summary>An order's shipping status, as the order service knows it (PLAN.md 4: orders aren't stored in the database).</summary>
public sealed record OrderStatus(string OrderNumber, string Status, string? Carrier, DateTimeOffset LastUpdate, string LastEvent);

public interface IOrderStatusService
{
    /// <summary>The order with <paramref name="orderNumber"/> (exact match), or null when there's none.</summary>
    Task<OrderStatus?> FindAsync(string orderNumber, CancellationToken cancellationToken);
}
