using Microsoft.Extensions.Options;
using SupportDesk.Data.Seeding;

namespace SupportDesk.UnitTests.Seeding;

public class SeedOrderStatusServiceTests
{
    private readonly SeedOrderStatusService _orders = new(Options.Create(new SeedOptions { Path = Path.Combine(AppContext.BaseDirectory, "seed") }));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Finds_a_seeded_order()
    {
        var order = await _orders.FindAsync("KO-205002", Ct);

        Assert.NotNull(order);
        Assert.Equal("Delayed", order.Status);
        Assert.Equal("FedEx", order.Carrier);
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 7, 30, 0, TimeSpan.Zero), order.LastUpdate);
        Assert.StartsWith("Weather delay", order.LastEvent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("KO-208815")]
    [InlineData("ko-205002")]
    public async Task An_order_not_in_the_file_is_null(string orderNumber)
    {
        Assert.Null(await _orders.FindAsync(orderNumber, Ct));
    }
}
