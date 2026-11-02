using Microsoft.Extensions.Configuration;
using SupportDesk.Core.Retrieval;
using SupportDesk.Data.Seeding;
using SupportDesk.EmbedFixtures;
using SupportDesk.Eval.Suite;

namespace SupportDesk.Eval.Tests;

/// <summary>The golden set itself: answerable with the sources retrieval returns, and written for the evaluation.</summary>
public class GoldenSetTests
{
    /// <summary>A golden ticket may share shorter runs of words with the seed data, such as "I'd like to know", but not this many.</summary>
    private const int CopiedWordsInARow = 6;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_expected_article_is_among_the_sources_retrieval_hands_the_model()
    {
        // As the reply retrieves: the nearest ReplyChunks chunks by cosine similarity, then those under MinSimilarity dropped
        var options = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build()
            .GetSection(RetrievalOptions.SectionName).Get<RetrievalOptions>()!;
        var tickets = await GoldenSet.ReadTicketsAsync(Path.Combine(GoldenData.Folder, GoldenSet.TicketsFile), Ct);
        var queries = (await EmbeddingFixtures.ReadAsync<QuestionFixture>(Path.Combine(GoldenData.Folder, GoldenSet.EmbeddingsFile), Ct))
            .Items.ToDictionary(q => q.Hash, q => q.Vector, StringComparer.Ordinal);
        var chunks = (await EmbeddingFixtures.ReadAsync<ArticleChunkFixture>(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "embeddings", EmbeddingFixtures.ArticlesFile), Ct)).Items;

        var unreachable = new List<string>();
        foreach (var ticket in tickets)
        {
            var query = queries[ArticleChunkDraft.HashOf(GoldenSet.QueryOf(ticket))];
            var retrieved = chunks.Select(c => (c.Slug, Similarity: Cosine(query, c.Vector)))
                .OrderByDescending(c => c.Similarity)
                .Take(options.ReplyChunks)
                .Where(c => c.Similarity >= options.MinSimilarity)
                .Select(c => c.Slug)
                .ToHashSet(StringComparer.Ordinal);
            unreachable.AddRange(ticket.ExpectedSlugs.Where(s => !retrieved.Contains(s)).Select(s => $"{ticket.Id}: {s}"));
        }

        Assert.True(unreachable.Count == 0, "Expected articles retrieval never returns, so no model could cite them:\n" + string.Join('\n', unreachable));
    }

    [Fact]
    public async Task Golden_tickets_are_not_copied_from_the_seed_tickets_articles_or_retrieval_questions()
    {
        var seed = Path.Combine(AppContext.BaseDirectory, "seed");
        var sources = (await SeedFiles.ReadTicketsAsync(seed, Ct)).SelectMany(t => new[] { t.Subject, t.Body })
            .Concat((await SeedFiles.ReadArticlesAsync(seed, Ct)).SelectMany(a => new[] { a.Title, a.BodyMarkdown }))
            .Concat((await EmbeddingFixtures.ReadQuestionsAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "retrieval-questions.json"), Ct)).Select(q => q.Question));
        var copied = sources.SelectMany(Runs).ToHashSet(StringComparer.Ordinal);
        var tickets = await GoldenSet.ReadTicketsAsync(Path.Combine(GoldenData.Folder, GoldenSet.TicketsFile), Ct);

        var copies = tickets.SelectMany(t => Runs(t.Subject).Concat(Runs(t.Body)).Where(copied.Contains).Select(run => $"{t.Id}: \"{run}\"")).ToList();

        Assert.True(copies.Count == 0, "Golden tickets repeat the seed data or articles word for word:\n" + string.Join('\n', copies));
    }

    [Fact]
    public void The_copy_check_finds_six_words_in_a_row_and_not_five()
    {
        var copied = Runs("Orders placed before 1 pm Mountain Time usually leave the same day.").ToHashSet(StringComparer.Ordinal);

        Assert.Contains(Runs("If orders placed before 1 PM Mountain time, when?"), copied.Contains);
        Assert.DoesNotContain(Runs("Were orders placed before 1 pm sent?"), copied.Contains);
    }

    private static IEnumerable<string> Runs(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string([.. w.Where(char.IsLetterOrDigit)]).ToLowerInvariant())
            .Where(w => w.Length > 0)
            .ToList();
        return Enumerable.Range(0, Math.Max(0, words.Count - CopiedWordsInARow + 1))
            .Select(i => string.Join(' ', words.Skip(i).Take(CopiedWordsInARow)));
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
