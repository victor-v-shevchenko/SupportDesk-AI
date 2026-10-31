using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Api.Endpoints;
using SupportDesk.Data.Seeding;

namespace SupportDesk.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public class SeedingTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fresh_database_is_seeded_with_every_ticket_and_article()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);
        await api.Client.GetAsync(new Uri("/api/health", UriKind.Relative), Ct);

        await using var db = api.CreateDbContext();
        var seedTickets = await SeedFiles.ReadTicketsAsync(SeedPath.Value, Ct);
        var seedArticles = await SeedFiles.ReadArticlesAsync(SeedPath.Value, Ct);
        Assert.Equal(seedTickets.Select(t => t.Number).Order(), (await db.Tickets.Select(t => t.Number).ToListAsync(Ct)).Order());
        Assert.Equal(seedArticles.Select(a => a.Slug).Order(), (await db.Articles.Select(a => a.Slug).ToListAsync(Ct)).Order());

        var stored = await db.Tickets.SingleAsync(t => t.Number == seedTickets[0].Number, Ct);
        Assert.Equal(seedTickets[0].Body, stored.Body);
        Assert.Equal(seedTickets[0].CreatedAt, stored.CreatedAt);
    }

    [Fact]
    public async Task Restarting_does_not_duplicate_seed_data()
    {
        await using var first = await SeededApi.StartAsync(postgres, Ct);
        await first.Client.GetAsync(new Uri("/api/health", UriKind.Relative), Ct);

        await using (var second = new ApiFactory(first.ConnectionString, "Development"))
        {
            using var client = second.CreateClient();
            await client.GetAsync(new Uri("/api/health", UriKind.Relative), Ct);
        }

        await using var db = first.CreateDbContext();
        var seedTickets = await SeedFiles.ReadTicketsAsync(SeedPath.Value, Ct);
        Assert.Equal(seedTickets.Count, await db.Tickets.CountAsync(Ct));
        Assert.Equal(12, await db.Articles.CountAsync(Ct));
    }

    [Fact]
    public async Task Restart_adds_back_a_missing_ticket_and_restores_a_changed_article()
    {
        await using var first = await SeededApi.StartAsync(postgres, Ct);
        await first.Client.GetAsync(new Uri("/api/health", UriKind.Relative), Ct);
        await using (var db = first.CreateDbContext())
        {
            await db.Tickets.Where(t => t.Number == "T-1005").ExecuteDeleteAsync(Ct);
            await db.Tickets.Where(t => t.Number == "T-1006").ExecuteUpdateAsync(s => s.SetProperty(t => t.Subject, "Edited by an agent"), Ct);
            await db.Articles.Where(a => a.Slug == "refunds").ExecuteUpdateAsync(s => s.SetProperty(a => a.BodyMarkdown, "stale"), Ct);
        }

        await using (var second = new ApiFactory(first.ConnectionString, "Development"))
        {
            using var client = second.CreateClient();
            await client.GetAsync(new Uri("/api/health", UriKind.Relative), Ct);
        }

        await using var check = first.CreateDbContext();
        Assert.Equal(25, await check.Tickets.CountAsync(Ct));
        Assert.True(await check.Tickets.AnyAsync(t => t.Number == "T-1005", Ct));
        // Tickets are user data: an existing one is never overwritten from the file
        Assert.Equal("Edited by an agent", (await check.Tickets.SingleAsync(t => t.Number == "T-1006", Ct)).Subject);
        var refunds = await check.Articles.SingleAsync(a => a.Slug == "refunds", Ct);
        Assert.StartsWith("This article explains how long refunds take", refunds.BodyMarkdown, StringComparison.Ordinal);
        Assert.Equal(12, await check.Articles.CountAsync(Ct));
    }

    [Fact]
    public async Task New_tickets_are_numbered_after_the_seeded_ones()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);

        var response = await api.Client.PostAsJsonAsync(
            new Uri("/api/tickets", UriKind.Relative),
            new CreateTicketRequest("Where is my order?", "It hasn't arrived.", "Jo Doe", null),
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<TicketDetailResponse>(JsonDefaults.Options, Ct);
        Assert.Equal("T-1026", created?.Number);
    }
}
