using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SupportDesk.Core.Retrieval;
using SupportDesk.Data.Seeding;

namespace SupportDesk.EmbedFixtures;

/// <summary>
/// The committed embedding fixtures (PLAN.md 11): <c>tests/fixtures/embeddings/articles.json</c> holds a vector per
/// article chunk, <c>questions.json</c> one per retrieval test question. Each item carries the hash of the text that was
/// embedded, so a test can tell when the fixtures no longer match the articles or the questions.
/// </summary>
public static class EmbeddingFixtures
{
    public const string FolderPath = "tests/fixtures/embeddings";
    public const string ArticlesFile = "articles.json";
    public const string QuestionsFile = "questions.json";
    public const string RetrievalQuestionsPath = "tests/fixtures/retrieval-questions.json";
    public const string RegenerateCommand = "dotnet run --project tools/EmbedFixtures";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Embeds every chunk of <paramref name="articles"/> and every question, in batches.</summary>
    public static async Task<(FixtureFile<ArticleChunkFixture> Articles, FixtureFile<QuestionFixture> Questions)> BuildAsync(
        IReadOnlyList<SeedArticle> articles,
        IReadOnlyList<RetrievalQuestion> questions,
        IEmbeddingGenerator<string, Embedding<float>> generator,
        string model,
        int dimensions,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(articles);
        ArgumentNullException.ThrowIfNull(questions);

        var chunks = articles.SelectMany(a => ArticleChunker.Chunk(a.BodyMarkdown).Select(c => (a.Slug, Chunk: c))).ToList();
        var chunkVectors = await EmbedAsync(generator, [.. chunks.Select(c => c.Chunk.EmbeddingInput)], model, dimensions, batchSize, cancellationToken);
        var questionVectors = await EmbedAsync(generator, [.. questions.Select(q => q.Question)], model, dimensions, batchSize, cancellationToken);

        return (
            new FixtureFile<ArticleChunkFixture>(model, dimensions,
                [.. chunks.Select((c, i) => new ArticleChunkFixture(c.Slug, c.Chunk.Ordinal, c.Chunk.ContentHash, chunkVectors[i]))]),
            new FixtureFile<QuestionFixture>(model, dimensions,
                [.. questions.Select((q, i) => new QuestionFixture(q.Question, ArticleChunkDraft.HashOf(q.Question), questionVectors[i]))]));
    }

    public static async Task<IReadOnlyList<RetrievalQuestion>> ReadQuestionsAsync(string path, CancellationToken cancellationToken) =>
        await ReadJsonAsync<List<RetrievalQuestion>>(path, cancellationToken);

    public static Task<FixtureFile<T>> ReadAsync<T>(string path, CancellationToken cancellationToken) =>
        ReadJsonAsync<FixtureFile<T>>(path, cancellationToken);

    /// <summary>One item per line, so a regenerated file shows in a diff as the chunks that changed.</summary>
    public static async Task WriteAsync<T>(string path, FixtureFile<T> file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        var text = new StringBuilder()
            .Append("{\n  \"model\": ").Append(JsonSerializer.Serialize(file.Model, JsonOptions))
            .Append(",\n  \"dimensions\": ").Append(file.Dimensions)
            .Append(",\n  \"items\": [\n    ")
            .AppendJoin(",\n    ", file.Items.Select(i => JsonSerializer.Serialize(i, JsonOptions)))
            .Append("\n  ]\n}\n");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, text.ToString(), cancellationToken);
    }

    private static async Task<List<float[]>> EmbedAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator, List<string> texts, string model, int dimensions, int batchSize, CancellationToken cancellationToken)
    {
        var vectors = new List<float[]>(texts.Count);
        foreach (var batch in texts.Chunk(batchSize))
        {
            var embeddings = await CheckedEmbeddings.GenerateAsync(generator, batch, model, dimensions, cancellationToken);
            vectors.AddRange(embeddings.Select(e => e.ToArray()));
        }

        return vectors;
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException($"{path} is empty.");
    }
}

public sealed record FixtureFile<T>(string Model, int Dimensions, IReadOnlyList<T> Items);

public sealed record ArticleChunkFixture(string Slug, int Ordinal, string Hash, float[] Vector);

public sealed record QuestionFixture(string Question, string Hash, float[] Vector);

/// <summary>An entry in <c>tests/fixtures/retrieval-questions.json</c>: a customer's question and the article that answers it.</summary>
public sealed record RetrievalQuestion(string Question, string ExpectedSlug);
