namespace SupportDesk.Core.Retrieval;

/// <summary>
/// Limits one embedding call (PLAN.md 6.5): embeddings aren't behind the chat pipeline, and the provider <c>HttpClient</c>
/// has no timeout beyond connecting. The limit runs on the injected <see cref="TimeProvider"/>.
/// </summary>
public static class EmbeddingTimeout
{
    /// <summary>
    /// Runs <paramref name="call"/> with a token that is cancelled after <paramref name="seconds"/> or when
    /// <paramref name="cancellationToken"/> is. Expiry throws <see cref="TimeoutException"/>; the caller's own cancellation
    /// stays an <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<T> RunAsync<T>(TimeProvider time, int seconds, Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(call);

        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(seconds), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            return await call(linked.Token);
        }
        catch (OperationCanceledException exception) when (limit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The embedding call took longer than {seconds} seconds (AI:Timeouts:EmbeddingSeconds).", exception);
        }
    }
}
