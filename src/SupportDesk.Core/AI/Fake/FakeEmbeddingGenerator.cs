using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace SupportDesk.Core.AI.Fake;

/// <summary>
/// A deterministic vector per text, from SHA-256 (PLAN.md 6.8): the same text always gives the same unit vector, on any
/// machine. It's for running the app without a model; similar texts don't get similar vectors, so retrieval tests use the
/// committed fixtures instead.
/// </summary>
public sealed class FakeEmbeddingGenerator(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
{
    public const string ProviderName = "fake";
    public const string ModelId = "fake-hash";

    private readonly EmbeddingGeneratorMetadata _metadata = new(ProviderName, defaultModelId: ModelId, defaultModelDimensions: dimensions);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        cancellationToken.ThrowIfCancellationRequested();

        var embeddings = values.Select(v => new Embedding<float>(Vector(v)) { ModelId = ModelId });
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceKey is not null ? null
            : serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata
            : serviceType.IsInstanceOfType(this) ? this
            : null;
    }

    public void Dispose()
    {
        // Nothing to release
    }

    /// <summary>SHA-256 of the text and a block counter, read as 16-bit values in -1…1, then scaled to length 1.</summary>
    private float[] Vector(string text)
    {
        var input = Encoding.UTF8.GetBytes(text);
        var block = new byte[input.Length + sizeof(int)];
        input.CopyTo(block, 0);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];

        var vector = new float[dimensions];
        const int valuesPerBlock = SHA256.HashSizeInBytes / sizeof(short);
        for (var i = 0; i < dimensions; i++)
        {
            if (i % valuesPerBlock == 0)
            {
                BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(input.Length), i / valuesPerBlock);
                SHA256.HashData(block, hash);
            }

            vector[i] = BinaryPrimitives.ReadInt16LittleEndian(hash[(i % valuesPerBlock * sizeof(short))..]) / (float)short.MaxValue;
        }

        var length = MathF.Sqrt(vector.Sum(v => v * v));
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= length;
        }

        return vector;
    }
}
