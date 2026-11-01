using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Fake;
using SupportDesk.Core.Articles;
using SupportDesk.Core.Retrieval;
using SupportDesk.Data.Seeding;

namespace SupportDesk.IntegrationTests.Retrieval;

/// <summary>
/// The startup sync (after seeding) against a real database, with the fake generator. Each test seeds from its own copy
/// of <c>seed/</c>, so it can edit an article between two starts the way someone editing the files would.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class EmbeddingSyncTests(PostgresFixture postgres) : IDisposable
{
    private readonly string _seed = CopySeed();
    private readonly RecordingEmbeddingGenerator _generator = new(new FakeEmbeddingGenerator(1024));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_first_start_embeds_every_chunk_and_a_second_start_embeds_nothing()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        var expected = await AllChunkInputsAsync();

        await StartAsync(connectionString);
        Assert.Equal(expected.Order(StringComparer.Ordinal), _generator.Texts.Order(StringComparer.Ordinal));

        _generator.Clear();
        await StartAsync(connectionString);
        Assert.Empty(_generator.Texts);
        await using var db = TestDatabase.CreateDbContext(connectionString);
        Assert.Equal(expected.Count, await db.ArticleChunks.CountAsync(Ct));
    }

    [Fact]
    public async Task Editing_one_article_reembeds_only_its_changed_chunks_and_removes_deleted_ones()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await StartAsync(connectionString);
        var file = Path.Combine(_seed, SeedFiles.ArticlesFolder, "shipping-times.md");
        var before = ArticleChunker.Chunk(SeedFiles.ParseArticle("shipping-times.md", await File.ReadAllTextAsync(file, Ct)).BodyMarkdown);

        // Rename one heading, change one section's text, delete the last section; the intro and one section stay as they were
        var text = (await File.ReadAllTextAsync(file, Ct))
            .Replace("## Tracking your order", "## Tracking a parcel", StringComparison.Ordinal)
            .Replace("contact us and we'll open a trace", "write to us and we'll open a trace", StringComparison.Ordinal);
        text = text[..text.IndexOf("## Lost packages", StringComparison.Ordinal)];
        await File.WriteAllTextAsync(file, text, Ct);
        var after = ArticleChunker.Chunk(SeedFiles.ParseArticle("shipping-times.md", text).BodyMarkdown);

        _generator.Clear();
        await StartAsync(connectionString);

        var changed = after.Where(c => !before.Any(b => b.ContentHash == c.ContentHash)).Select(c => c.EmbeddingInput).ToList();
        Assert.Equal(2, changed.Count);
        Assert.Equal(changed.Order(StringComparer.Ordinal), _generator.Texts.Order(StringComparer.Ordinal));

        await using var db = TestDatabase.CreateDbContext(connectionString);
        var stored = await db.ArticleChunks.Where(c => c.Article!.Slug == "shipping-times").OrderBy(c => c.Ordinal).ToListAsync(Ct);
        Assert.Equal(after.Select(c => (c.Ordinal, c.Heading, c.ContentHash)), stored.Select(c => (c.Ordinal, c.Heading, c.ContentHash)));
        Assert.Equal((await AllChunkInputsAsync()).Count, await db.ArticleChunks.CountAsync(Ct));
    }

    [Fact]
    public async Task Moving_a_section_keeps_its_vector_and_updates_its_position()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await StartAsync(connectionString);
        var file = Path.Combine(_seed, SeedFiles.ArticlesFolder, "shipping-times.md");
        var text = await File.ReadAllTextAsync(file, Ct);
        var lost = text[text.IndexOf("## Lost packages", StringComparison.Ordinal)..];
        var intro = text.IndexOf("## Delivery options", StringComparison.Ordinal);
        await File.WriteAllTextAsync(file, text[..intro] + lost.TrimEnd() + "\n\n" + text[intro..text.IndexOf("## Lost packages", StringComparison.Ordinal)], Ct);

        _generator.Clear();
        await StartAsync(connectionString);

        Assert.Empty(_generator.Texts);
        await using var db = TestDatabase.CreateDbContext(connectionString);
        var headings = await db.ArticleChunks.Where(c => c.Article!.Slug == "shipping-times").OrderBy(c => c.Ordinal).Select(c => c.Heading).ToListAsync(Ct);
        Assert.Equal([null, "Lost packages", "Delivery options in the United States", "Tracking your order", "Delayed shipments"], headings);
    }

    [Fact]
    public async Task Changing_the_embedding_model_reembeds_every_chunk()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await StartAsync(connectionString);

        _generator.Clear();
        await StartAsync(connectionString, new Dictionary<string, string?> { ["AI:Embeddings:Model"] = "another-model" });

        var expected = await AllChunkInputsAsync();
        Assert.Equal(expected.Count, _generator.Texts.Count);
        await using var db = TestDatabase.CreateDbContext(connectionString);
        Assert.Equal(["another-model"], await db.ArticleChunks.Select(c => c.EmbeddingModel).Distinct().ToListAsync(Ct));
        Assert.Equal(expected.Count, await db.ArticleChunks.CountAsync(Ct));
    }

    [Fact]
    public async Task A_wrong_vector_dimension_fails_the_sync_with_a_clear_error_and_stores_nothing()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await StartAsync(connectionString);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SyncWithAsync(connectionString, new FakeEmbeddingGenerator(768)));

        Assert.Contains("returned 768-dimension vectors, but AI:Embeddings:Dimensions is 1024", error.Message, StringComparison.Ordinal);
        await using var db = TestDatabase.CreateDbContext(connectionString);
        Assert.Equal(["fake-hash"], await db.ArticleChunks.Select(c => c.EmbeddingModel).Distinct().ToListAsync(Ct));
    }

    [Fact]
    public async Task An_unreachable_embedding_provider_fails_the_sync_with_a_hint()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await StartAsync(connectionString);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SyncWithAsync(connectionString, new UnreachableEmbeddingGenerator()));

        Assert.Contains("Couldn't embed the article chunks with Fake 'another-model' at startup", error.Message, StringComparison.Ordinal);
        Assert.Contains("AI__Embeddings__Provider=Fake", error.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _generator.Dispose();
        Directory.Delete(_seed, recursive: true);
    }

    /// <summary>Starts the API (migrate, seed, sync), then stops it.</summary>
    private async Task StartAsync(string connectionString, Dictionary<string, string?>? extra = null)
    {
        await using var factory = new ApiFactory(connectionString, "Development", Settings(extra),
            services: services => services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_generator));
        using var client = factory.CreateClient();
    }

    /// <summary>
    /// Runs the sync on an already seeded database with <paramref name="generator"/> and a new model name, so every chunk
    /// needs embedding. Called directly rather than through startup: when startup fails inside WebApplicationFactory, the
    /// exception that reaches the test races with the app disposing its services (docs/ai-log.md, step 3b).
    /// </summary>
    private async Task SyncWithAsync(string connectionString, IEmbeddingGenerator<string, Embedding<float>> generator)
    {
        await using var factory = new ApiFactory(connectionString, "Development", Settings(),
            services: services => services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_generator));
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var aiOptions = new AiOptions { Embeddings = { Provider = EmbeddingProvider.Fake, Model = "another-model" } };
        var sync = new EmbeddingSyncService(
            scope.ServiceProvider.GetRequiredService<IArticleRepository>(),
            scope.ServiceProvider.GetRequiredService<IArticleChunkStore>(),
            generator,
            Options.Create(aiOptions),
            scope.ServiceProvider.GetRequiredService<IOptions<RetrievalOptions>>(),
            NullLogger<EmbeddingSyncService>.Instance);

        await sync.SyncAsync(Ct);
    }

    private Dictionary<string, string?> Settings(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>(extra ?? []) { ["Seed:Path"] = _seed };
        return settings;
    }

    private async Task<List<string>> AllChunkInputsAsync() =>
        [.. (await SeedFiles.ReadArticlesAsync(_seed, Ct)).SelectMany(a => ArticleChunker.Chunk(a.BodyMarkdown)).Select(c => c.EmbeddingInput)];

    private static string CopySeed()
    {
        var target = Path.Combine(Path.GetTempPath(), "supportdesk-seed-" + Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.GetFiles(SeedPath.Value, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(SeedPath.Value, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return target;
    }
}

/// <summary>Fails like an embedding provider that isn't running.</summary>
internal sealed class UnreachableEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new HttpRequestException("Connection refused (localhost:11434)");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        // Nothing to release
    }
}
