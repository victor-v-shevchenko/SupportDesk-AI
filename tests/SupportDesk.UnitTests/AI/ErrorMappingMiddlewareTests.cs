using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using SupportDesk.Core.AI;

namespace SupportDesk.UnitTests.AI;

public class ErrorMappingMiddlewareTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<HttpStatusCode, bool> StatusCodes => new()
    {
        { HttpStatusCode.TooManyRequests, true },
        { HttpStatusCode.InternalServerError, true },
        { HttpStatusCode.ServiceUnavailable, true },
        { (HttpStatusCode)529, true },
        { HttpStatusCode.RequestTimeout, true },
        { HttpStatusCode.BadRequest, false },
        { HttpStatusCode.Unauthorized, false },
        { HttpStatusCode.Forbidden, false },
        { HttpStatusCode.NotFound, false },
    };

    [Theory]
    [MemberData(nameof(StatusCodes))]
    public async Task Http_errors_become_ModelUnavailable_with_the_status_and_whether_to_retry(HttpStatusCode status, bool transient)
    {
        var client = new ErrorMappingMiddleware(new ThrowingClient(new HttpRequestException("body with details", null, status)), classifier: null);

        var error = await Assert.ThrowsAsync<ModelUnavailableException>(() => client.GetResponseAsync("hi", cancellationToken: Ct));

        Assert.Equal((int)status, error.StatusCode);
        Assert.Equal(transient, error.IsTransient);
        Assert.DoesNotContain("body with details", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connection_error_is_transient_without_a_status()
    {
        var client = new ErrorMappingMiddleware(new ThrowingClient(new HttpRequestException("connection refused")), classifier: null);

        var error = await Assert.ThrowsAsync<ModelUnavailableException>(() => client.GetResponseAsync("hi", cancellationToken: Ct));

        Assert.Null(error.StatusCode);
        Assert.True(error.IsTransient);
    }

    [Fact]
    public async Task An_unknown_exception_still_comes_out_as_ModelUnavailable_and_keeps_the_original_inside()
    {
        var original = new InvalidOperationException("SDK bug");
        var client = new ErrorMappingMiddleware(new ThrowingClient(original), classifier: null);

        var error = await Assert.ThrowsAsync<ModelUnavailableException>(() => client.GetResponseAsync("hi", cancellationToken: Ct));

        Assert.False(error.IsTransient);
        Assert.Same(original, error.InnerException);
    }

    [Fact]
    public async Task The_provider_classifier_decides_for_its_own_exceptions()
    {
        var client = new ErrorMappingMiddleware(new ThrowingClient(new SdkException()), new SdkClassifier());

        var error = await Assert.ThrowsAsync<ModelUnavailableException>(() => client.GetResponseAsync("hi", cancellationToken: Ct));

        Assert.Equal(529, error.StatusCode);
        Assert.True(error.IsTransient);
    }

    [Fact]
    public async Task Caller_cancellation_stays_OperationCanceled()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await cancellation.CancelAsync();
        var client = new ErrorMappingMiddleware(new ThrowingClient(new OperationCanceledException(cancellation.Token)), classifier: null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync("hi", cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task A_cancellation_the_caller_did_not_ask_for_is_a_transient_timeout()
    {
        // What HttpClient and the SDKs throw when their own timeout fires
        var client = new ErrorMappingMiddleware(new ThrowingClient(new TaskCanceledException("timed out", new TimeoutException())), classifier: null);

        var error = await Assert.ThrowsAsync<ModelUnavailableException>(() => client.GetResponseAsync("hi", cancellationToken: Ct));

        Assert.True(error.IsTransient);
        Assert.Contains("timed out", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Typed_exceptions_pass_through_unchanged()
    {
        var refused = new ModelRefusedException();
        var client = new ErrorMappingMiddleware(new ThrowingClient(refused), classifier: null);

        Assert.Same(refused, await Assert.ThrowsAsync<ModelRefusedException>(() => client.GetResponseAsync("hi", cancellationToken: Ct)));
    }

    [Fact]
    public async Task A_content_filter_finish_is_a_refusal_that_keeps_the_usage()
    {
        var usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 0 };
        var client = new ErrorMappingMiddleware(
            new UpdatesClient(new ChatResponseUpdate { FinishReason = ChatFinishReason.ContentFilter, Contents = [new UsageContent(usage)] }),
            classifier: null);

        var error = await Assert.ThrowsAsync<ModelRefusedException>(() => client.GetResponseAsync("hi", cancellationToken: Ct));

        Assert.Equal(10, error.Usage?.InputTokenCount);
    }

    [Fact]
    public async Task A_streamed_refusal_passes_the_remaining_updates_on_then_throws()
    {
        var client = new ErrorMappingMiddleware(
            new UpdatesClient(
                new ChatResponseUpdate(ChatRole.Assistant, "Hel"),
                new ChatResponseUpdate { FinishReason = ChatFinishReason.ContentFilter },
                new ChatResponseUpdate { Contents = [new UsageContent(new UsageDetails { OutputTokenCount = 2 })] }),
            classifier: null);

        var received = new List<ChatResponseUpdate>();
        await Assert.ThrowsAsync<ModelRefusedException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync("hi", cancellationToken: Ct))
            {
                received.Add(update);
            }
        });

        Assert.Equal(3, received.Count);
        Assert.Single(received[2].Contents.OfType<UsageContent>());
    }

    [Fact]
    public async Task An_error_in_the_middle_of_a_stream_comes_after_the_items_already_sent()
    {
        var client = new ErrorMappingMiddleware(
            new UpdatesClient([new ChatResponseUpdate(ChatRole.Assistant, "Hel")], thenThrow: new HttpRequestException("overloaded", null, HttpStatusCode.ServiceUnavailable)),
            classifier: null);

        var received = new List<string>();
        var error = await Assert.ThrowsAsync<ModelUnavailableException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync("hi", cancellationToken: Ct))
            {
                received.Add(update.Text);
            }
        });

        Assert.Equal(["Hel"], received);
        Assert.Equal(503, error.StatusCode);
    }

    private sealed class SdkException : Exception;

    private sealed class SdkClassifier : IModelErrorClassifier
    {
        public ModelError? Classify(Exception exception) => exception is SdkException ? ModelError.FromStatus(529) : null;
    }

    private sealed class ThrowingClient(Exception exception) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromException<ChatResponse>(exception);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw exception;

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
            // Nothing to release
        }
    }

    private sealed class UpdatesClient(ChatResponseUpdate[] updates, Exception? thenThrow = null) : IChatClient
    {
        public UpdatesClient(params ChatResponseUpdate[] updates)
            : this(updates, null)
        {
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in updates)
            {
                await Task.Yield();
                yield return update;
            }

            if (thenThrow is not null)
            {
                throw thenThrow;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
            // Nothing to release
        }
    }
}
