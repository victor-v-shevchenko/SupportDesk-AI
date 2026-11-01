using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace SupportDesk.Api.AI;

/// <summary>
/// Voyage AI embeddings over HTTP (<c>POST /v1/embeddings</c>), since there's no official .NET package (PLAN.md 6.1).
/// <see cref="EmbeddingGenerationOptions.AdditionalProperties"/> can set <c>input_type</c> (<c>query</c> or <c>document</c>).
/// </summary>
public sealed class VoyageEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public const string ProviderName = "voyage";
    public const string InputTypeKey = "input_type";

    private static readonly Uri DefaultEndpoint = new("https://api.voyageai.com/v1/");

    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly int _dimensions;
    private readonly EmbeddingGeneratorMetadata _metadata;

    public VoyageEmbeddingGenerator(HttpClient httpClient, string apiKey, string model, int dimensions)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
        _httpClient.BaseAddress ??= DefaultEndpoint;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _model = model;
        _dimensions = dimensions;
        _metadata = new EmbeddingGeneratorMetadata(ProviderName, _httpClient.BaseAddress, model, dimensions);
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var request = new VoyageRequest(
            [.. values],
            options?.ModelId ?? _model,
            options?.Dimensions ?? _dimensions,
            options?.AdditionalProperties?.TryGetValue(InputTypeKey, out string? inputType) == true ? inputType : null);

        using var response = await _httpClient.PostAsJsonAsync("embeddings", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<VoyageResponse>(cancellationToken)
            ?? throw new InvalidDataException("Voyage returned an empty response.");

        var embeddings = body.Data.OrderBy(d => d.Index).Select(d => new Embedding<float>(d.Embedding) { ModelId = body.Model });
        return new GeneratedEmbeddings<Embedding<float>>(embeddings)
        {
            Usage = body.Usage is { } usage ? new UsageDetails { InputTokenCount = usage.TotalTokens, TotalTokenCount = usage.TotalTokens } : null,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(EmbeddingGeneratorMetadata))
        {
            return _metadata;
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed record VoyageRequest(
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("output_dimension")] int OutputDimension,
        [property: JsonPropertyName("input_type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InputType);

    private sealed record VoyageResponse(
        [property: JsonPropertyName("data")] IReadOnlyList<VoyageEmbedding> Data,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("usage")] VoyageUsage? Usage);

    private sealed record VoyageEmbedding(
        [property: JsonPropertyName("embedding")] float[] Embedding,
        [property: JsonPropertyName("index")] int Index);

    private sealed record VoyageUsage([property: JsonPropertyName("total_tokens")] long TotalTokens);
}
