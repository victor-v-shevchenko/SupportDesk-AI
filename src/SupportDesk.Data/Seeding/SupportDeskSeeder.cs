using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportDesk.Core.Articles;
using SupportDesk.Core.Tickets;

namespace SupportDesk.Data.Seeding;

/// <summary>
/// Loads <c>seed/</c> into the database. Safe to run on every start: tickets are matched on number and only missing ones
/// are added (existing tickets are user data and are left alone); articles are matched on slug, added when missing and
/// updated when the file changed.
/// </summary>
internal sealed partial class SupportDeskSeeder(
    SupportDeskDbContext db,
    IOptions<SeedOptions> options,
    TimeProvider time,
    ILogger<SupportDeskSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var seedPath = options.Value.ResolvedPath;
        var tickets = await SeedFiles.ReadTicketsAsync(seedPath, cancellationToken);
        var articles = await SeedFiles.ReadArticlesAsync(seedPath, cancellationToken);

        var addedTickets = await AddMissingTicketsAsync(tickets, cancellationToken);
        var (addedArticles, updatedArticles) = await UpsertArticlesAsync(articles, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await MoveTicketNumbersPastExistingAsync(cancellationToken);

        LogSeeded(addedTickets, addedArticles, updatedArticles);
    }

    private async Task<int> AddMissingTicketsAsync(IReadOnlyList<SeedTicket> tickets, CancellationToken cancellationToken)
    {
        var existing = (await db.Tickets.Select(t => t.Number).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var missing = tickets.Where(t => !existing.Contains(t.Number)).ToList();
        db.Tickets.AddRange(missing.Select(t => new Ticket
        {
            Number = t.Number,
            Subject = t.Subject,
            Body = t.Body,
            CustomerName = t.CustomerName,
            OrderNumber = t.OrderNumber,
            Status = t.Status,
            CreatedAt = t.CreatedAt,
        }));
        return missing.Count;
    }

    private async Task<(int Added, int Updated)> UpsertArticlesAsync(IReadOnlyList<SeedArticle> articles, CancellationToken cancellationToken)
    {
        var existing = await db.Articles.ToDictionaryAsync(a => a.Slug, StringComparer.Ordinal, cancellationToken);
        var now = time.GetUtcNow();
        int added = 0, updated = 0;
        foreach (var article in articles)
        {
            if (!existing.TryGetValue(article.Slug, out var stored))
            {
                db.Articles.Add(new Article { Slug = article.Slug, Title = article.Title, BodyMarkdown = article.BodyMarkdown, UpdatedAt = now });
                added++;
            }
            else if (stored.Title != article.Title || stored.BodyMarkdown != article.BodyMarkdown)
            {
                stored.Title = article.Title;
                stored.BodyMarkdown = article.BodyMarkdown;
                stored.UpdatedAt = now;
                updated++;
            }
        }

        return (added, updated);
    }

    /// <summary>Seeded tickets carry their own numbers, so new tickets must continue after the highest one.</summary>
    private async Task MoveTicketNumbersPastExistingAsync(CancellationToken cancellationToken)
    {
        var numbers = await db.Tickets.Select(t => t.Number).ToListAsync(cancellationToken);
        var highest = numbers
            .Select(n => n.StartsWith("T-", StringComparison.Ordinal) && long.TryParse(n.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0)
            .DefaultIfEmpty(0)
            .Max();

        // Only moves forward: setval when the sequence's next value would collide with an existing number
        await db.Database.ExecuteSqlAsync(
            $"""
            SELECT setval('"ticket_numbers"', {highest})
            WHERE {highest} >= (SELECT CASE WHEN is_called THEN last_value + 1 ELSE last_value END FROM "ticket_numbers")
            """,
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Seed data loaded: {AddedTickets} tickets added, {AddedArticles} articles added, {UpdatedArticles} articles updated")]
    private partial void LogSeeded(int addedTickets, int addedArticles, int updatedArticles);
}
