using Microsoft.Extensions.AI;

namespace SupportDesk.Core.Retrieval;

/// <summary>Embeds texts and checks that the provider returned one vector per text, each of the configured dimension.</summary>
public static class CheckedEmbeddings
{
    public static async Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        IReadOnlyList<string> texts,
        string model,
        int dimensions,
        CancellationToken cancellationToken)
    {
        var embeddings = await generator.GenerateAsync(texts, cancellationToken: cancellationToken);
        if (embeddings.Count != texts.Count)
        {
            throw new InvalidOperationException(
                $"The embedding model '{model}' returned {embeddings.Count} vectors for {texts.Count} texts.");
        }

        var wrong = embeddings.FirstOrDefault(e => e.Vector.Length != dimensions);
        if (wrong is not null)
        {
            throw new InvalidOperationException(
                $"The embedding model '{model}' returned {wrong.Vector.Length}-dimension vectors, but AI:Embeddings:Dimensions is {dimensions} " +
                $"(the size of the ArticleChunks.Embedding column). Use a model that returns {dimensions} dimensions.");
        }

        return [.. embeddings.Select(e => e.Vector)];
    }
}
