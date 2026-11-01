using Microsoft.Extensions.AI;

namespace SupportDesk.Core.AI;

/// <summary>
/// The provider couldn't answer: it returned an error, timed out or couldn't be reached (PLAN.md 6.5). The message names the
/// status code at most, never the provider's response body, so it's safe to log; it still never goes to the client.
/// The <see cref="Exception.InnerException"/> is the SDK's own and its message can echo the request (ticket text), so log
/// <see cref="StatusCode"/> and the inner exception's type, not <c>ToString()</c>, unless a debug setting allows it.
/// </summary>
public sealed class ModelUnavailableException : Exception
{
    public ModelUnavailableException()
        : this("The model provider is unavailable.")
    {
    }

    public ModelUnavailableException(string message)
        : this(message, null)
    {
    }

    public ModelUnavailableException(string message, Exception? innerException)
        : this(message, innerException, statusCode: null, isTransient: false)
    {
    }

    public ModelUnavailableException(string message, Exception? innerException, int? statusCode, bool isTransient)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        IsTransient = isTransient;
    }

    /// <summary>The HTTP status the provider returned, or null for connection errors and timeouts.</summary>
    public int? StatusCode { get; }

    /// <summary>Whether trying again could work (429, 5xx, overloaded, timeouts, connection errors); the retry middleware reads it.</summary>
    public bool IsTransient { get; }

    /// <summary>How long the provider asked us to wait (<c>Retry-After</c>), when it said and the SDK lets us see it.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>Set when the pipeline gave up waiting, rather than the provider failing.</summary>
    public bool IsTimeout { get; init; }
}

/// <summary>The provider declined the request, for example an Anthropic response with <c>stop_reason = "refusal"</c> (PLAN.md 6.7). Never retried.</summary>
public sealed class ModelRefusedException : Exception
{
    public ModelRefusedException()
        : this("The model declined the request.")
    {
    }

    public ModelRefusedException(string message)
        : this(message, null)
    {
    }

    public ModelRefusedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The refused call's usage, when the provider reported it, so it can still be logged and paid for.</summary>
    public UsageDetails? Usage { get; init; }
}

/// <summary>
/// A call was refused before it reached the provider (PLAN.md 6.4): its input estimate is over
/// <c>AI:Budget:MaxInputTokensPerCall</c>, or the request has used more than <c>AI:Budget:MaxTokensPerRequest</c>. Never retried.
/// </summary>
public sealed class BudgetExceededException : Exception
{
    public BudgetExceededException()
        : this("The model call is over the token budget.")
    {
    }

    public BudgetExceededException(string message)
        : this(message, null)
    {
    }

    public BudgetExceededException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public long Tokens { get; init; }

    public long Limit { get; init; }
}
