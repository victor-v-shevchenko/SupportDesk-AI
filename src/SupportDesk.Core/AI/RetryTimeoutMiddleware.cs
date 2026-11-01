using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Microsoft.Extensions.AI;

namespace SupportDesk.Core.AI;

/// <summary>
/// Retries and timeouts (PLAN.md 6.5). Only transient <see cref="ModelUnavailableException"/>s are retried, with exponential
/// backoff and jitter or the provider's <c>Retry-After</c>, up to <c>AI:Retry:MaxAttempts</c>. A stream is retried only
/// until its first item (text or a tool call) has gone to the caller; updates before that are held back, so a failed
/// attempt leaves nothing behind. The caller's cancellation is never retried and never reported as a timeout.
/// </summary>
/// <remarks>
/// Timeouts cancel the inner call's token, so they rely on the provider honoring cancellation, as the SDKs'
/// <see cref="HttpClient"/> calls do. Every timer is a <see cref="CancellationTokenSource"/> on the injected
/// <see cref="TimeProvider"/>, disposed when its attempt ends.
/// </remarks>
public sealed class RetryTimeoutMiddleware(IChatClient innerClient, AiOptions settings, TimeProvider timeProvider) : DelegatingChatClient(innerClient)
{
    private readonly AiRetrySettings _retry = settings.Retry;
    private readonly AiTimeoutSettings _timeouts = settings.Timeouts;

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            Exception failure;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_timeouts.NonStreamingSeconds), timeProvider))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
            {
                try
                {
                    return await base.GetResponseAsync(messages, options, linked.Token);
                }
                catch (Exception exception)
                {
                    failure = Normalize(exception, timedOut: timeout.IsCancellationRequested ? "the response" : null, transientTimeout: true, cancellationToken);
                }
            }

            var delay = RetryDelay(failure, attempt);
            if (delay is null)
            {
                ExceptionDispatchInfo.Throw(failure);
            }

            await Task.Delay(delay.Value, timeProvider, cancellationToken);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(_timeouts.StreamTotalSeconds), timeProvider);
        for (var attempt = 1; ; attempt++)
        {
            using var itemTimer = new CancellationTokenSource(Timeout.InfiniteTimeSpan, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, total.Token, itemTimer.Token);
            var held = new List<ChatResponseUpdate>();
            var sentFirstItem = false;
            var active = false;
            Exception? failure = null;

            var updates = base.GetStreamingResponseAsync(messages, options, linked.Token).GetAsyncEnumerator(linked.Token);
            try
            {
                // The first-item timer runs from the start of the attempt; empty updates (a "message started" event) don't reset it
                itemTimer.CancelAfter(TimeSpan.FromSeconds(_timeouts.FirstItemSeconds));
                while (true)
                {
                    if (active)
                    {
                        itemTimer.CancelAfter(TimeSpan.FromSeconds(_timeouts.GapSeconds));
                    }

                    bool hasNext;
                    try
                    {
                        hasNext = await updates.MoveNextAsync();
                    }
                    catch (Exception exception)
                    {
                        failure = Normalize(
                            exception,
                            timedOut: StreamTimeout(total, itemTimer, active),
                            transientTimeout: !sentFirstItem && !total.IsCancellationRequested,
                            cancellationToken);
                        break;
                    }

                    if (!hasNext)
                    {
                        break;
                    }

                    var update = updates.Current;
                    if (!active && (IsItem(update) || IsProgress(update)))
                    {
                        // From here on, the gap between updates is what's timed
                        active = true;
                    }

                    // Stopped before anything goes to the caller: the caller's time with an update counts against
                    // neither the first-item wait nor the gap
                    if (active)
                    {
                        itemTimer.CancelAfter(Timeout.InfiniteTimeSpan);
                    }

                    if (sentFirstItem)
                    {
                        yield return update;
                        continue;
                    }

                    held.Add(update);
                    if (IsItem(update))
                    {
                        sentFirstItem = true;
                        foreach (var waiting in held)
                        {
                            yield return waiting;
                        }

                        held.Clear();
                    }
                }
            }
            finally
            {
                await updates.DisposeAsync();
            }

            if (failure is null)
            {
                // A stream that ended without an item (only usage, or a refusal): pass on what it sent
                foreach (var waiting in held)
                {
                    yield return waiting;
                }

                yield break;
            }

            var delay = sentFirstItem || total.IsCancellationRequested ? null : RetryDelay(failure, attempt);
            if (delay is null)
            {
                ExceptionDispatchInfo.Throw(failure);
            }

            await WaitAsync(delay.Value, total, cancellationToken);
        }
    }

    /// <summary>Which wait timed out, or null when none did.</summary>
    private static string? StreamTimeout(CancellationTokenSource total, CancellationTokenSource itemTimer, bool active)
    {
        if (total.IsCancellationRequested)
        {
            return "the whole stream";
        }

        if (!itemTimer.IsCancellationRequested)
        {
            return null;
        }

        return active ? "the next update" : "the first item";
    }

    /// <summary>Text or a tool call: what the caller shows or acts on, so after it the stream can't be retried.</summary>
    public static bool IsItem(ChatResponseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        return update.Contents.Any(c => c is TextContent { Text.Length: > 0 } or FunctionCallContent);
    }

    /// <summary>Thinking text shows the model is working, so it ends the first-item wait, but it's never shown and doesn't stop retries.</summary>
    private static bool IsProgress(ChatResponseUpdate update) =>
        update.Contents.Any(c => c is TextReasoningContent { Text.Length: > 0 });

    /// <summary>What the caller sees for <paramref name="exception"/>: their own cancellation, a timeout, or the typed exception.</summary>
    private static Exception Normalize(Exception exception, string? timedOut, bool transientTimeout, CancellationToken callerToken)
    {
        if (callerToken.IsCancellationRequested)
        {
            return exception as OperationCanceledException ?? new OperationCanceledException(callerToken);
        }

        if (timedOut is not null)
        {
            return new ModelUnavailableException($"The model call timed out waiting for {timedOut}.", exception, statusCode: null, transientTimeout) { IsTimeout = true };
        }

        return exception switch
        {
            ModelUnavailableException or ModelRefusedException or BudgetExceededException => exception,
            _ => new ModelUnavailableException("The model call failed.", exception),
        };
    }

    /// <summary>How long to wait before the next attempt, or null when this failure isn't retried.</summary>
    private TimeSpan? RetryDelay(Exception failure, int attempt)
    {
        if (failure is not ModelUnavailableException { IsTransient: true } unavailable || attempt >= _retry.MaxAttempts)
        {
            return null;
        }

        var maxDelay = TimeSpan.FromMilliseconds(_retry.MaxDelayMs);
        if (unavailable.RetryAfter is { } retryAfter)
        {
            // Waiting longer than the cap would leave the user looking at nothing; better to fail now
            return retryAfter <= maxDelay ? retryAfter : null;
        }

        // Exponential, with jitter in the upper half so two callers spread out but neither retries at once
        var backoff = _retry.BaseDelayMs * Math.Pow(2, attempt - 1);
        var jittered = backoff * (0.5 + (RandomNumberGenerator.GetInt32(0, 1000) / 2000.0));
        return TimeSpan.FromMilliseconds(Math.Min(jittered, maxDelay.TotalMilliseconds));
    }

    private async Task WaitAsync(TimeSpan delay, CancellationTokenSource total, CancellationToken callerToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, total.Token);
        try
        {
            await Task.Delay(delay, timeProvider, linked.Token);
        }
        catch (OperationCanceledException exception) when (!callerToken.IsCancellationRequested)
        {
            throw new ModelUnavailableException("The model call timed out waiting for the whole stream.", exception, statusCode: null, isTransient: false) { IsTimeout = true };
        }
    }
}
