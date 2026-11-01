namespace SupportDesk.Core.AI;

/// <summary>What <see cref="ErrorMappingMiddleware"/> needs to know about a provider exception.</summary>
public sealed record ModelError(int? StatusCode, bool IsTransient, TimeSpan? RetryAfter = null)
{
    /// <summary>A connection error: the request may not have reached the provider. Transient.</summary>
    public static ModelError Connection { get; } = new(null, IsTransient: true);

    /// <summary>The retry rules in PLAN.md 6.5: 408, 429 and 5xx (including Anthropic's 529 "overloaded") are transient; other codes aren't.</summary>
    public static ModelError FromStatus(int statusCode) =>
        new(statusCode, statusCode is 408 or 429 or >= 500);
}

/// <summary>
/// Recognizes one provider SDK's exceptions. The implementations live in the API's composition root, the only code that
/// references provider SDKs; exceptions none of them recognize still come out of the pipeline typed (see <see cref="ErrorMappingMiddleware"/>).
/// </summary>
public interface IModelErrorClassifier
{
    /// <summary>The error <paramref name="exception"/> stands for, or null when it isn't this provider's.</summary>
    ModelError? Classify(Exception exception);
}
