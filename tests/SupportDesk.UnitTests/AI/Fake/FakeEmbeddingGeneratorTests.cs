using Microsoft.Extensions.AI;
using SupportDesk.Core.AI.Fake;

namespace SupportDesk.UnitTests.AI.Fake;

public class FakeEmbeddingGeneratorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_same_text_gives_the_same_1024_dimension_unit_vector()
    {
        using var generator = new FakeEmbeddingGenerator(1024);
        using var other = new FakeEmbeddingGenerator(1024);

        var first = await generator.GenerateVectorAsync("How long does shipping take?", cancellationToken: Ct);
        var again = await other.GenerateVectorAsync("How long does shipping take?", cancellationToken: Ct);

        Assert.Equal(1024, first.Length);
        Assert.True(first.Span.SequenceEqual(again.Span));
        var length = Math.Sqrt(first.ToArray().Sum(v => (double)v * v));
        Assert.Equal(1.0, length, precision: 5);
    }

    [Fact]
    public async Task Different_texts_give_different_vectors()
    {
        using var generator = new FakeEmbeddingGenerator(1024);

        var embeddings = await generator.GenerateAsync(["shipping", "Shipping", ""], cancellationToken: Ct);

        Assert.Equal(3, embeddings.Count);
        Assert.False(embeddings[0].Vector.Span.SequenceEqual(embeddings[1].Vector.Span));
        Assert.False(embeddings[0].Vector.Span.SequenceEqual(embeddings[2].Vector.Span));
    }

    [Fact]
    public async Task Vectors_do_not_change_between_versions()
    {
        // Pinned values: if these change, every stored fake embedding is stale
        using var generator = new FakeEmbeddingGenerator(1024);

        var vector = await generator.GenerateVectorAsync("Kestrel", cancellationToken: Ct);

        Assert.Equal(Pinned, vector.Span[..3].ToArray().Select(v => MathF.Round(v, 6)));
    }

    private static readonly float[] Pinned = [-0.04766f, 0.007497f, -0.025198f];
}
