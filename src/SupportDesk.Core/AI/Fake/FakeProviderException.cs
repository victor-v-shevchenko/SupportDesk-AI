using System.Net;
using System.Net.Http;

namespace SupportDesk.Core.AI.Fake;

/// <summary>What the fake provider throws for an <c>error</c> step: an HTTP error, as a real adapter's would be, plus <c>Retry-After</c>.</summary>
public sealed class FakeProviderException : HttpRequestException
{
    public FakeProviderException()
    {
    }

    public FakeProviderException(string message)
        : base(message)
    {
    }

    public FakeProviderException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public FakeProviderException(string message, HttpStatusCode statusCode, TimeSpan? retryAfter)
        : base(message, inner: null, statusCode)
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan? RetryAfter { get; }
}

/// <summary>Reads the status and <c>Retry-After</c> from <see cref="FakeProviderException"/>.</summary>
public sealed class FakeErrorClassifier : IModelErrorClassifier
{
    public ModelError? Classify(Exception exception) => exception switch
    {
        FakeProviderException { StatusCode: { } status } fake => ModelError.FromStatus((int)status) with { RetryAfter = fake.RetryAfter },
        _ => null,
    };
}
