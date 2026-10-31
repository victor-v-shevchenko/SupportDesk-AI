using SupportDesk.Core.Tickets;

namespace SupportDesk.Data.Seeding;

/// <summary>
/// One entry in <c>seed/tickets.json</c>. <see cref="Category"/> and <see cref="Priority"/> are the labels the ticket was
/// written for; they show the seed covers every value and aren't stored (classification starts as NotRun).
/// </summary>
public sealed record SeedTicket(
    string Number,
    string Subject,
    string Body,
    string CustomerName,
    string? OrderNumber,
    TicketStatus Status,
    DateTimeOffset CreatedAt,
    Category Category,
    Priority Priority);

/// <summary>One entry in <c>seed/orders.json</c>, read by the order service from step 8; orders aren't stored in the database.</summary>
public sealed record SeedOrder(string OrderNumber, string Status, string? Carrier, DateTimeOffset LastUpdate, string LastEvent);

/// <summary>A parsed <c>seed/articles/*.md</c> file: front matter plus the Markdown after it.</summary>
public sealed record SeedArticle(string Slug, string Title, string BodyMarkdown);
