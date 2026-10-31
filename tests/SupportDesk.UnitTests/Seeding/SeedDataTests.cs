using System.Text.RegularExpressions;
using SupportDesk.Api.Endpoints;
using SupportDesk.Core.Tickets;
using SupportDesk.Data.Seeding;

namespace SupportDesk.UnitTests.Seeding;

/// <summary>Checks that the committed seed files give later steps what they need (PLAN.md 4, step 2 spec).</summary>
public partial class SeedDataTests
{
    private static readonly string SeedPath = Path.Combine(AppContext.BaseDirectory, "seed");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Tickets_cover_every_category_and_priority()
    {
        var tickets = await SeedFiles.ReadTicketsAsync(SeedPath, Ct);

        Assert.InRange(tickets.Count, 20, 30);
        Assert.Equal(Enum.GetValues<Category>(), tickets.Select(t => t.Category).Distinct().Order());
        Assert.Equal(Enum.GetValues<Priority>(), tickets.Select(t => t.Priority).Distinct().Order());
    }

    [Fact]
    public async Task Tickets_have_unique_numbers_and_would_pass_create_validation()
    {
        var tickets = await SeedFiles.ReadTicketsAsync(SeedPath, Ct);

        Assert.Equal(tickets.Count, tickets.Select(t => t.Number).Distinct().Count());
        Assert.All(tickets, t =>
        {
            Assert.Matches(TicketNumberRegex(), t.Number);
            var (_, errors) = CreateTicketValidator.Validate(new CreateTicketRequest(t.Subject, t.Body, t.CustomerName, t.OrderNumber));
            Assert.Empty(errors);
        });
    }

    [Fact]
    public async Task At_least_ten_tickets_have_order_numbers_and_exactly_one_is_not_in_orders()
    {
        var tickets = await SeedFiles.ReadTicketsAsync(SeedPath, Ct);
        var orders = (await SeedFiles.ReadOrdersAsync(SeedPath, Ct)).Select(o => o.OrderNumber).ToHashSet(StringComparer.Ordinal);

        var withOrders = tickets.Where(t => t.OrderNumber is not null).ToList();

        Assert.True(withOrders.Count >= 10, $"Only {withOrders.Count} tickets have an order number.");
        Assert.Single(withOrders, t => !orders.Contains(t.OrderNumber!));
    }

    [Fact]
    public async Task Two_tickets_contain_a_prompt_injection_and_one_tries_to_close_the_ticket_tag()
    {
        var tickets = await SeedFiles.ReadTicketsAsync(SeedPath, Ct);

        var injections = tickets.Where(t => t.Body.Contains("ignore previous instructions", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.Equal(2, injections.Count);
        Assert.Single(injections, t => t.Body.Contains("</ticket>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Orders_are_ten_unique_valid_numbers_covering_every_status()
    {
        var orders = await SeedFiles.ReadOrdersAsync(SeedPath, Ct);

        Assert.Equal(10, orders.Count);
        Assert.Equal(10, orders.Select(o => o.OrderNumber).Distinct().Count());
        Assert.All(orders, o => Assert.True(OrderNumber.IsValid(o.OrderNumber), o.OrderNumber));
        Assert.Equal(["Cancelled", "Delayed", "Delivered", "Processing", "Shipped"], orders.Select(o => o.Status).Distinct().Order(StringComparer.Ordinal));
        // Only orders that have left the warehouse have a carrier
        Assert.All(orders, o => Assert.Equal(o.Status is "Shipped" or "Delivered" or "Delayed", o.Carrier is not null));
    }

    [Fact]
    public async Task Twelve_articles_each_with_front_matter_matching_the_file_and_two_sections()
    {
        var files = Directory.GetFiles(Path.Combine(SeedPath, SeedFiles.ArticlesFolder), "*.md");

        Assert.Equal(12, files.Length);
        foreach (var file in files)
        {
            var article = SeedFiles.ParseArticle(Path.GetFileName(file), await File.ReadAllTextAsync(file, Ct));
            Assert.Equal(Path.GetFileNameWithoutExtension(file), article.Slug);
            Assert.Matches(SlugRegex(), article.Slug);
            var sections = article.BodyMarkdown.Split('\n').Count(line => line.StartsWith("## ", StringComparison.Ordinal));
            Assert.True(sections >= 2, $"{article.Slug} has {sections} '##' sections.");
        }
    }

    [GeneratedRegex(@"^T-[0-9]{4}\z")]
    private static partial Regex TicketNumberRegex();

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*\z")]
    private static partial Regex SlugRegex();
}
