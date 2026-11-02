using System.Text.Json;
using System.Text.Json.Serialization;
using SupportDesk.Core.Replies;
using SupportDesk.Core.Retrieval;
using SupportDesk.Core.Tickets;

namespace SupportDesk.EmbedFixtures;

/// <summary>
/// The evaluation's golden tickets (PLAN.md 11), <c>tests/SupportDesk.Eval/golden/tickets.json</c>, and the embedding of
/// each ticket's retrieval query in <c>golden/embeddings.json</c>, made by the same model as the article fixtures.
/// </summary>
public static class GoldenSet
{
    public const string FolderPath = "tests/SupportDesk.Eval/golden";
    public const string TicketsFile = "tickets.json";
    public const string EmbeddingsFile = "embeddings.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<IReadOnlyList<GoldenTicket>> ReadTicketsAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<GoldenTicket>>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException($"{path} is empty.");
    }

    /// <summary>The text the reply embeds to retrieve sources for <paramref name="ticket"/>, built exactly as the reply builds it.</summary>
    public static string QueryOf(GoldenTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        return ReplyPromptBuilder.BuildQuery(new Ticket
        {
            Number = ticket.Id,
            Subject = ticket.Subject.Trim(),
            Body = ticket.Body,
            CustomerName = ticket.CustomerName,
        });
    }

    /// <summary>One item per golden ticket: its query, the query's hash and its vector.</summary>
    public static FixtureFile<QuestionFixture> ToFixture(IReadOnlyList<GoldenTicket> tickets, IReadOnlyList<float[]> vectors, string model, int dimensions)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(vectors);

        return new FixtureFile<QuestionFixture>(model, dimensions, [.. tickets.Select((t, i) =>
        {
            var query = QueryOf(t);
            return new QuestionFixture(query, ArticleChunkDraft.HashOf(query), vectors[i]);
        })]);
    }
}

/// <summary>
/// A ticket of the golden set with what a good answer looks like: its category and priority, and the articles a good reply
/// cites (empty when the knowledge base doesn't cover it, and the reply should say so).
/// </summary>
public sealed record GoldenTicket(
    string Id,
    string Subject,
    string Body,
    string CustomerName,
    Category ExpectedCategory,
    Priority ExpectedPriority,
    IReadOnlyList<string> ExpectedSlugs,
    GoldenInjection? Injection = null);

/// <summary>
/// What an injection case tries: <c>category</c> (to set <see cref="Category"/>), <c>system-prompt</c> (to reveal it),
/// <c>task</c> (to create a follow-up task), or <c>citation</c> (to cite a made-up article named <see cref="Text"/>).
/// </summary>
public sealed record GoldenInjection(string Kind, Category? Category = null, string? Text = null)
{
    public const string CategoryKind = "category";
    public const string SystemPromptKind = "system-prompt";
    public const string TaskKind = "task";
    public const string CitationKind = "citation";
}
