using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Api.Endpoints;
using SupportDesk.EmbedFixtures;

namespace SupportDesk.IntegrationTests.Retrieval;

/// <summary>
/// Search over the committed mxbai-embed-large vectors in PostgreSQL: the startup sync stores the fixture vectors for the
/// chunks, and each question is embedded to its fixture vector, so this measures the real model's retrieval, without it.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public class RetrievalQualityTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_expected_article_is_in_the_top_3_for_every_question_and_first_for_at_least_12()
    {
        var generator = await FixtureEmbeddingGenerator.LoadAsync(Ct);
        await using var api = await SeededApi.StartAsync(
            postgres,
            Ct,
            new Dictionary<string, string?> { ["AI:Embeddings:Model"] = generator.Model },
            services => services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(generator));
        var questions = await EmbeddingFixtures.ReadQuestionsAsync(FixturePaths.RetrievalQuestions, Ct);

        var misses = new List<string>();
        var first = 0;
        foreach (var question in questions)
        {
            var results = await api.Client.GetFromJsonAsync<List<SearchResultResponse>>(
                $"/api/search?q={Uri.EscapeDataString(question.Question)}&k=3", Ct);

            var slugs = results!.Select(r => r.ArticleSlug).ToList();
            if (slugs.FirstOrDefault() == question.ExpectedSlug)
            {
                first++;
            }

            if (!slugs.Contains(question.ExpectedSlug))
            {
                misses.Add($"'{question.Question}': expected {question.ExpectedSlug}, got {string.Join(", ", slugs)}");
            }
        }

        Assert.True(misses.Count == 0, "Not in the top 3:\n" + string.Join('\n', misses));
        Assert.True(first >= 12, $"First for {first} of {questions.Count} questions; at least 12 expected.");
    }

    [Fact]
    public async Task The_sync_stored_the_committed_vectors()
    {
        var generator = await FixtureEmbeddingGenerator.LoadAsync(Ct);
        await using var api = await SeededApi.StartAsync(
            postgres,
            Ct,
            new Dictionary<string, string?> { ["AI:Embeddings:Model"] = generator.Model },
            services => services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(generator));
        await api.Client.GetAsync(new Uri("/api/health", UriKind.Relative), Ct);

        var fixture = await EmbeddingFixtures.ReadAsync<ArticleChunkFixture>(FixturePaths.Articles, Ct);
        await using var db = api.CreateDbContext();
        var stored = await db.ArticleChunks.Include(c => c.Article)
            .Select(c => new { c.Article!.Slug, c.Ordinal, c.ContentHash, c.EmbeddingModel, c.Embedding })
            .ToListAsync(Ct);

        Assert.Equal(fixture.Items.Count, stored.Count);
        Assert.All(stored, c => Assert.Equal("mxbai-embed-large", c.EmbeddingModel));
        var expected = fixture.Items.Single(i => i.Slug == "shipping-times" && i.Ordinal == 1);
        var row = stored.Single(c => c.Slug == "shipping-times" && c.Ordinal == 1);
        Assert.Equal(expected.Hash, row.ContentHash);
        Assert.Equal(expected.Vector, row.Embedding.ToArray());
    }

    [Fact]
    public async Task Results_are_ordered_by_similarity_and_none_is_below_the_minimum()
    {
        var generator = await FixtureEmbeddingGenerator.LoadAsync(Ct);
        await using var api = await SeededApi.StartAsync(
            postgres,
            Ct,
            new Dictionary<string, string?> { ["AI:Embeddings:Model"] = generator.Model, ["Retrieval:MinSimilarity"] = "0.6" },
            services => services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(generator));
        var question = (await EmbeddingFixtures.ReadQuestionsAsync(FixturePaths.RetrievalQuestions, Ct))[0].Question;

        var results = await api.Client.GetFromJsonAsync<List<SearchResultResponse>>($"/api/search?q={Uri.EscapeDataString(question)}&k=10", Ct);

        Assert.NotNull(results);
        Assert.NotEmpty(results);
        Assert.True(results.Count < 10, "the 0.6 minimum should drop some of the 10 nearest");
        Assert.All(results, r => Assert.True(r.Similarity >= 0.6, $"{r.Similarity}"));
        Assert.Equal(results.OrderByDescending(r => r.Similarity).Select(r => r.Similarity), results.Select(r => r.Similarity));
        Assert.Equal("account-and-password", results[0].ArticleSlug);
        Assert.False(string.IsNullOrWhiteSpace(results[0].ArticleTitle));
        Assert.False(string.IsNullOrWhiteSpace(results[0].Text));
    }
}
