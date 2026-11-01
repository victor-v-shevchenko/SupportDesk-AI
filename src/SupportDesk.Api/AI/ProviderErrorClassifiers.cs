using System.ClientModel;
using Anthropic.Exceptions;
using OllamaSharp.Models.Exceptions;
using SupportDesk.Core.AI;

namespace SupportDesk.Api.AI;

/// <summary>Anthropic SDK exceptions. An error event inside a stream (<c>overloaded_error</c>, <c>api_error</c>) is treated as transient.</summary>
public sealed class AnthropicErrorClassifier : IModelErrorClassifier
{
    public ModelError? Classify(Exception exception) => exception switch
    {
        AnthropicApiException api => ModelError.FromStatus((int)api.StatusCode),
        AnthropicIOException => ModelError.Connection,
        AnthropicSseException => new ModelError(null, IsTransient: true),
        AnthropicException => new ModelError(null, IsTransient: false),
        _ => null,
    };
}

/// <summary>
/// OpenAI SDK exceptions. <see cref="ClientResultException.Status"/> is 0 when no response came back. The raw response
/// carries <c>Retry-After</c>, in seconds; the Anthropic SDK's exceptions don't expose headers, so only OpenAI has it.
/// </summary>
public sealed class OpenAIErrorClassifier : IModelErrorClassifier
{
    public ModelError? Classify(Exception exception) => exception switch
    {
        ClientResultException { Status: 0 } => ModelError.Connection,
        ClientResultException result => ModelError.FromStatus(result.Status) with { RetryAfter = RetryAfter(result) },
        _ => null,
    };

    private static TimeSpan? RetryAfter(ClientResultException exception) =>
        exception.GetRawResponse()?.Headers.TryGetValue("retry-after", out var value) == true
        && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
        && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
}

/// <summary>OllamaSharp's own exceptions: an error the server sent in the response (unknown model, no tool support). HTTP errors are <see cref="HttpRequestException"/>s, which the pipeline handles itself.</summary>
public sealed class OllamaErrorClassifier : IModelErrorClassifier
{
    public ModelError? Classify(Exception exception) => exception switch
    {
        OllamaException => new ModelError(null, IsTransient: false),
        _ => null,
    };
}
