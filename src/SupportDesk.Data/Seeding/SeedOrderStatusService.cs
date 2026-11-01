using Microsoft.Extensions.Options;
using SupportDesk.Core.Orders;

namespace SupportDesk.Data.Seeding;

/// <summary>
/// The fake order system (PLAN.md 4): <c>seed/orders.json</c>, read on the first lookup and kept in memory. A load that
/// failed is tried again on the next lookup. Registered as a singleton.
/// </summary>
public sealed class SeedOrderStatusService(IOptions<SeedOptions> options) : IOrderStatusService
{
    private Task<Dictionary<string, OrderStatus>>? _load;

    public async Task<OrderStatus?> FindAsync(string orderNumber, CancellationToken cancellationToken)
    {
        // The load itself isn't cancelled: another lookup may be waiting on it
        var orders = await Load().WaitAsync(cancellationToken);
        return orders.GetValueOrDefault(orderNumber);
    }

    /// <summary>The one load in progress or done; a failed one is replaced, by only one of the lookups that find it failed.</summary>
    private Task<Dictionary<string, OrderStatus>> Load()
    {
        var current = Volatile.Read(ref _load);
        if (current is { IsFaulted: false, IsCanceled: false })
        {
            return current;
        }

        var next = LoadAsync(options.Value.ResolvedPath);
        return Interlocked.CompareExchange(ref _load, next, current) == current ? next : _load!;
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
