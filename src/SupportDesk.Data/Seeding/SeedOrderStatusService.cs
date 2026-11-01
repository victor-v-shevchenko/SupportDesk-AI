using Microsoft.Extensions.Options;
using SupportDesk.Core.Orders;

namespace SupportDesk.Data.Seeding;

/// <summary>
/// The fake order system (PLAN.md 4): <c>seed/orders.json</c>, read once on the first lookup and kept in memory.
/// Registered as a singleton.
/// </summary>
public sealed class SeedOrderStatusService(IOptions<SeedOptions> options) : IOrderStatusService
{
    private readonly Lazy<Task<Dictionary<string, OrderStatus>>> _orders = new(() => LoadAsync(options.Value.ResolvedPath));

    public async Task<OrderStatus?> FindAsync(string orderNumber, CancellationToken cancellationToken)
    {
        // The load itself isn't cancelled: another lookup may be waiting on it
        var orders = await _orders.Value.WaitAsync(cancellationToken);
        return orders.GetValueOrDefault(orderNumber);
    }

    private static async Task<Dictionary<string, OrderStatus>> LoadAsync(string seedPath)
    {
        var orders = await SeedFiles.ReadOrdersAsync(seedPath, CancellationToken.None);
        return orders.ToDictionary(
            o => o.OrderNumber,
            o => new OrderStatus(o.OrderNumber, o.Status, o.Carrier, o.LastUpdate, o.LastEvent),
            StringComparer.Ordinal);
    }
}
