using SupportDesk.Core.Retrieval;
using SupportDesk.Data.Seeding;
using SupportDesk.EmbedFixtures;

namespace SupportDesk.IntegrationTests.Retrieval;

/// <summary>The committed vectors are only worth something while they were made from the current chunks and questions.</summary>
public class FixtureHashTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Committed_fixtures_match_the_current_chunks_and_questions()
    {
        var articles = await SeedFiles.ReadArticlesAsync(SeedPath.Value, Ct);
        var questions = await EmbeddingFixtures.ReadQuestionsAsync(FixturePaths.RetrievalQuestions, Ct);

        var problems = await CheckAsync(articles, questions);

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    [Fact]
    public async Task The_fixtures_are_mxbai_embed_large_vectors_of_1024_dimensions()
    {
        var file = await EmbeddingFixtures.ReadAsync<ArticleChunkFixture>(FixturePaths.Articles, Ct);
        var questions = await EmbeddingFixtures.ReadAsync<QuestionFixture>(FixturePaths.Questions, Ct);

        Assert.Equal("mxbai-embed-large", file.Model);
        Assert.Equal("mxbai-embed-large", questions.Model);
        Assert.All(file.Items.Concat<object>(questions.Items), item =>
            Assert.Equal(1024, item is ArticleChunkFixture a ? a.Vector.Length : ((QuestionFixture)item).Vector.Length));
    }

    [Fact]
    public async Task An_edited_article_fails_the_check_with_a_message_naming_the_tool()
    {
        var articles = (await SeedFiles.ReadArticlesAsync(SeedPath.Value, Ct)).ToList();
        articles[0] = articles[0] with { BodyMarkdown = articles[0].BodyMarkdown + "\n\nOne more sentence." };
        var questions = await EmbeddingFixtures.ReadQuestionsAsync(FixturePaths.RetrievalQuestions, Ct);

        var problems = await CheckAsync(articles, questions);

        var problem = Assert.Single(problems);
        Assert.Contains(articles[0].Slug, problem, StringComparison.Ordinal);
        Assert.Contains("dotnet run --project tools/EmbedFixtures", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edited_question_fails_the_check()
    {
        var articles = await SeedFiles.ReadArticlesAsync(SeedPath.Value, Ct);
        var questions = (await EmbeddingFixtures.ReadQuestionsAsync(FixturePaths.RetrievalQuestions, Ct)).ToList();
        questions[0] = questions[0] with { Question = questions[0].Question + "?" };

        var problems = await CheckAsync(articles, questions);

        Assert.Contains("dotnet run --project tools/EmbedFixtures", Assert.Single(problems), StringComparison.Ordinal);
    }

    /// <summary>Compares (slug, ordinal, hash) per chunk and the hash per question with the committed files.</summary>
    private static async Task<List<string>> CheckAsync(IReadOnlyList<SeedArticle> articles, IReadOnlyList<RetrievalQuestion> questions)
    {
        var problems = new List<string>();
        var regenerate = $"Run `{EmbeddingFixtures.RegenerateCommand}` (with Ollama and mxbai-embed-large) and commit the result.";

        var fixture = await EmbeddingFixtures.ReadAsync<ArticleChunkFixture>(FixturePaths.Articles, Ct);
        var stored = fixture.Items.ToLookup(i => i.Slug, StringComparer.Ordinal);
        foreach (var article in articles)
        {
            var expected = ArticleChunker.Chunk(article.BodyMarkdown).Select(c => (c.Ordinal, c.ContentHash));
            var actual = stored[article.Slug].OrderBy(i => i.Ordinal).Select(i => (i.Ordinal, i.Hash));
            if (!expected.SequenceEqual(actual))
            {
                problems.Add($"{EmbeddingFixtures.ArticlesFile}: the chunks of '{article.Slug}' changed since the fixtures were made. {regenerate}");
            }
        }

        var unknown = stored.Select(g => g.Key).Except(articles.Select(a => a.Slug), StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            problems.Add($"{EmbeddingFixtures.ArticlesFile} has articles that no longer exist: {string.Join(", ", unknown)}. {regenerate}");
        }

        var questionFixture = await EmbeddingFixtures.ReadAsync<QuestionFixture>(FixturePaths.Questions, Ct);
        if (!questions.Select(q => ArticleChunkDraft.HashOf(q.Question)).SequenceEqual(questionFixture.Items.Select(i => i.Hash), StringComparer.Ordinal))
        {
            problems.Add($"{EmbeddingFixtures.QuestionsFile} doesn't match retrieval-questions.json. {regenerate}");
        }

        return problems;
    }
}
