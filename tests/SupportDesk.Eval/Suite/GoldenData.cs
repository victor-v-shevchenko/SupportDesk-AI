using Microsoft.Extensions.AI;
using SupportDesk.Core.Retrieval;
using SupportDesk.EmbedFixtures;

namespace SupportDesk.Eval.Suite;

/// <summary>
/// The golden tickets and the vectors retrieval uses for them: the committed article fixtures and the golden queries'
/// embeddings. Loading fails early when the two weren't made by the same model, or when a ticket changed since its query
/// was embedded.
/// </summary>
public sealed class GoldenData
{
    private GoldenData(IReadOnlyList<GoldenTicket> tickets, FixtureEmbeddingGenerator embeddings)
    {
        Tickets = tickets;
        Embeddings = embeddings;
    }

    public IReadOnlyList<GoldenTicket> Tickets { get; }

    public FixtureEmbeddingGenerator Embeddings { get; }

    public static string Folder { get; } = Path.Combine(AppContext.BaseDirectory, "golden");

    public static async Task<GoldenData> LoadAsync(CancellationToken cancellationToken)
    {
        var tickets = await GoldenSet.ReadTicketsAsync(Path.Combine(Folder, GoldenSet.TicketsFile), cancellationToken);
        var queries = await EmbeddingFixtures.ReadAsync<QuestionFixture>(Path.Combine(Folder, GoldenSet.EmbeddingsFile), cancellationToken);
        var articles = await EmbeddingFixtures.ReadAsync<ArticleChunkFixture>(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "embeddings", EmbeddingFixtures.ArticlesFile), cancellationToken);

        Check(tickets, queries, articles);
        var vectors = articles.Items.Select(i => (i.Hash, i.Vector)).Concat(queries.Items.Select(i => (i.Hash, i.Vector)))
            .DistinctBy(i => i.Hash, StringComparer.Ordinal)
            .ToDictionary(i => i.Hash, i => i.Vector, StringComparer.Ordinal);
        return new GoldenData(tickets, new FixtureEmbeddingGenerator(articles.Model, vectors));
    }

    /// <summary>Throws, naming the command that fixes it, when the golden embeddings don't match the article fixtures or the tickets.</summary>
    public static void Check(IReadOnlyList<GoldenTicket> tickets, FixtureFile<QuestionFixture> queries, FixtureFile<ArticleChunkFixture> articles)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(articles);

        if (!string.Equals(queries.Model, articles.Model, StringComparison.Ordinal) || queries.Dimensions != articles.Dimensions)
        {
            throw new InvalidOperationException(
                $"The golden embeddings were made with {queries.Model} ({queries.Dimensions} dimensions), but the article fixtures with "
                + $"{articles.Model} ({articles.Dimensions}). Regenerate both with `{EmbeddingFixtures.RegenerateCommand}`.");
        }

        var hashes = queries.Items.Select(i => i.Hash).ToHashSet(StringComparer.Ordinal);
        var stale = tickets.Where(t => !hashes.Contains(ArticleChunkDraft.HashOf(GoldenSet.QueryOf(t)))).Select(t => t.Id).ToList();
        if (stale.Count > 0)
        {
            throw new InvalidOperationException(
                $"The golden embeddings don't match these tickets: {string.Join(", ", stale)}. Regenerate them with `{EmbeddingFixtures.RegenerateCommand}`.");
        }
    }
}

/// <summary>Returns the committed vector for each text, looked up by its hash; a text without one is an error.</summary>
public sealed class FixtureEmbeddingGenerator(string model, IReadOnlyDictionary<string, float[]> vectors) : IEmbeddingGenerator<string, Embedding<float>>
{
    public string Model { get; } = model;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var embeddings = values.Select(text => vectors.TryGetValue(ArticleChunkDraft.HashOf(text), out var vector)
            ? new Embedding<float>(vector) { ModelId = Model }
            : throw new InvalidOperationException($"No committed vector for a text the app embedded. Run `{EmbeddingFixtures.RegenerateCommand}`."));
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
        // Nothing to release
    }
}
