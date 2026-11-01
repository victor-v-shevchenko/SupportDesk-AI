using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using SupportDesk.Core.Retrieval;
using SupportDesk.EmbedFixtures;

namespace SupportDesk.IntegrationTests.Retrieval;

/// <summary>Passes calls to another generator and records every text it was asked to embed.</summary>
public sealed class RecordingEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    private readonly ConcurrentQueue<string> _texts = new();

    public IReadOnlyList<string> Texts => [.. _texts];

    public void Clear() => _texts.Clear();

    public override Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = values.ToList();
        foreach (var value in list)
        {
            _texts.Enqueue(value);
        }

        return base.GenerateAsync(list, options, cancellationToken);
    }
}

/// <summary>
/// Returns the committed fixture vector for each text, looked up by the hash the fixture stores, so retrieval tests run
/// on real <c>mxbai-embed-large</c> vectors without a model. A text without a fixture fails the test.
/// </summary>
public sealed class FixtureEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly Dictionary<string, float[]> _vectors;

    private FixtureEmbeddingGenerator(string model, Dictionary<string, float[]> vectors)
    {
        Model = model;
        _vectors = vectors;
    }

    public string Model { get; }

    public static async Task<FixtureEmbeddingGenerator> LoadAsync(CancellationToken cancellationToken)
    {
        var articles = await EmbeddingFixtures.ReadAsync<ArticleChunkFixture>(FixturePaths.Articles, cancellationToken);
        var questions = await EmbeddingFixtures.ReadAsync<QuestionFixture>(FixturePaths.Questions, cancellationToken);
        var vectors = articles.Items.Select(i => (i.Hash, i.Vector)).Concat(questions.Items.Select(i => (i.Hash, i.Vector)))
            .DistinctBy(i => i.Hash, StringComparer.Ordinal)
            .ToDictionary(i => i.Hash, i => i.Vector, StringComparer.Ordinal);
        return new FixtureEmbeddingGenerator(articles.Model, vectors);
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var embeddings = values.Select(text => _vectors.TryGetValue(ArticleChunkDraft.HashOf(text), out var vector)
            ? new Embedding<float>(vector) { ModelId = Model }
            : throw new InvalidOperationException($"No fixture vector for '{text}'. Run `{EmbeddingFixtures.RegenerateCommand}`."));
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
        // Nothing to release
    }
}

/// <summary>Where the build copies the committed fixtures, next to the test assembly.</summary>
public static class FixturePaths
{
    public static string Articles { get; } = Path.Combine(AppContext.BaseDirectory, "fixtures", "embeddings", EmbeddingFixtures.ArticlesFile);

    public static string Questions { get; } = Path.Combine(AppContext.BaseDirectory, "fixtures", "embeddings", EmbeddingFixtures.QuestionsFile);

    public static string RetrievalQuestions { get; } = Path.Combine(AppContext.BaseDirectory, "fixtures", "retrieval-questions.json");
}
