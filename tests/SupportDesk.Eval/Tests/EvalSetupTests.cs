using System.Reflection;
using Microsoft.Extensions.Configuration;
using SupportDesk.Core.AI;
using SupportDesk.Core.Retrieval;
using SupportDesk.EmbedFixtures;
using SupportDesk.Eval.Suite;

namespace SupportDesk.Eval.Tests;

/// <summary>What stops a run before it starts, and what keeps the model-calling tests out of CI.</summary>
public class EvalSetupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_fake_provider_is_refused_with_a_clear_message()
    {
        var error = Assert.Throws<InvalidOperationException>(() => EvalSettings.From(Configuration(new() { ["AI:Chat:Provider"] = "Fake" })));

        Assert.Contains("AI:Chat:Provider is Fake", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_resolve_the_model_the_prompts_and_the_dry_run_limit()
    {
        var settings = EvalSettings.From(Configuration(new()
        {
            ["AI:Chat:Provider"] = "Ollama",
            ["AI:Chat:Model"] = "qwen2.5:7b",
            ["Eval:Limit"] = "3",
            ["Eval:SaveReplies"] = "true",
        }));

        Assert.Equal(new EvalSettings(ChatProvider.Ollama, "qwen2.5:7b", settings.Prompts, 3, SaveReplies: true), settings);
        Assert.False(EvalSettings.From(Configuration(new() { ["AI:Chat:Provider"] = "Ollama" })).SaveReplies);
        Assert.Equal("reply.v2", settings.Prompts["reply"]);
        Assert.Equal("classify.v2", settings.Prompts["classify"]);
        Assert.Equal("claude-opus-5-5", EvalSettings.From(Configuration(new() { ["AI:Chat:Provider"] = "Anthropic" })).Model);
    }

    [Fact]
    public void A_limit_below_one_is_refused() =>
        Assert.Throws<InvalidOperationException>(() => EvalSettings.From(Configuration(new() { ["AI:Chat:Provider"] = "Ollama", ["Eval:Limit"] = "0" })));

    [Fact]
    public async Task The_committed_golden_set_matches_its_embeddings()
    {
        var golden = await GoldenData.LoadAsync(Ct);

        Assert.InRange(golden.Tickets.Count, 25, 40);
        Assert.True(golden.Tickets.Count(t => t.Injection is not null) >= 6);
        Assert.Equal(golden.Tickets.Count, golden.Tickets.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("mxbai-embed-large", golden.Embeddings.Model);
    }

    [Fact]
    public async Task Golden_embeddings_from_another_model_fail_early_naming_the_command()
    {
        var (tickets, queries, articles) = await ReadGoldenAsync();

        var error = Assert.Throws<InvalidOperationException>(() => GoldenData.Check(tickets, queries with { Model = "voyage-3.5" }, articles));

        Assert.Contains("voyage-3.5", error.Message, StringComparison.Ordinal);
        Assert.Contains(EmbeddingFixtures.RegenerateCommand, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ticket_changed_since_its_query_was_embedded_fails_early()
    {
        var (tickets, queries, articles) = await ReadGoldenAsync();
        List<GoldenTicket> changed = [tickets[0] with { Body = tickets[0].Body + " Thanks!" }, .. tickets.Skip(1)];

        var error = Assert.Throws<InvalidOperationException>(() => GoldenData.Check(changed, queries, articles));

        Assert.Contains(tickets[0].Id, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Article_fixtures_older_than_the_articles_fail_early_naming_the_command()
    {
        var (_, _, articles) = await ReadGoldenAsync();
        string[] current = [.. articles.Items.Skip(1).Select(i => i.Hash), ArticleChunkDraft.HashOf("A paragraph added since.")];

        var error = Assert.Throws<InvalidOperationException>(() => GoldenData.CheckArticles(current, articles));

        Assert.Contains(EmbeddingFixtures.RegenerateCommand, error.Message, StringComparison.Ordinal);
        GoldenData.CheckArticles([.. articles.Items.Select(i => i.Hash)], articles);
    }

    [Fact]
    public void Every_test_class_that_can_call_a_model_carries_the_Eval_trait()
    {
        // CI runs this project with Category!=Eval, so a model-calling test without the trait would run there by accident
        var modelCalling = new[] { typeof(Suite.EvalSuiteTests), typeof(TokenEstimateTests) };
        var testClasses = typeof(EvalSetupTests).Assembly.GetTypes()
            .Where(t => t.GetMethods().Any(m => m.GetCustomAttributes<FactAttribute>().Any()))
            .ToList();

        Assert.All(modelCalling, t => Assert.Contains(t, testClasses));
        Assert.All(modelCalling, t => Assert.Contains(
            t.GetCustomAttributes<TraitAttribute>(),
            a => a.Name == "Category" && a.Value == "Eval"));
        // Everything else is a unit test in Tests/, with no model and no container
        Assert.All(testClasses.Except(modelCalling), t => Assert.Equal("SupportDesk.Eval.Tests", t.Namespace));
    }

    private static async Task<(IReadOnlyList<GoldenTicket>, FixtureFile<QuestionFixture>, FixtureFile<ArticleChunkFixture>)> ReadGoldenAsync() => (
        await GoldenSet.ReadTicketsAsync(Path.Combine(GoldenData.Folder, GoldenSet.TicketsFile), Ct),
        await EmbeddingFixtures.ReadAsync<QuestionFixture>(Path.Combine(GoldenData.Folder, GoldenSet.EmbeddingsFile), Ct),
        await EmbeddingFixtures.ReadAsync<ArticleChunkFixture>(Path.Combine(AppContext.BaseDirectory, "fixtures", "embeddings", EmbeddingFixtures.ArticlesFile), Ct));

    private static IConfiguration Configuration(Dictionary<string, string?> values)
    {
        var all = new Dictionary<string, string?>
        {
            ["AI:Prompts:reply"] = "reply.v2",
            ["AI:Prompts:classify"] = "classify.v2",
        };
        foreach (var (key, value) in values)
        {
            all[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(all).Build();
    }
}
