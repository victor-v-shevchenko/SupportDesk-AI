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
