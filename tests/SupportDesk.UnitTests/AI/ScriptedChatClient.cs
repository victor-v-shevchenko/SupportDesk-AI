using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using SupportDesk.Core.AI.Fake;

namespace SupportDesk.UnitTests.AI;

/// <summary>
/// A provider whose attempts are scripted one by one, so tests can fail then succeed, which the stateless fake provider
/// can't. Each attempt is a function of the call's token; the last one repeats. It records each call, its options and its messages.
/// </summary>
internal sealed class ScriptedChatClient(TimeProvider time, params Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>[] attempts) : IChatClient
{
    private readonly List<(DateTimeOffset At, ChatOptions? Options, IReadOnlyList<ChatMessage> Messages)> _calls = [];

    public int Calls
    {
        get
        {
            lock (_calls)
            {
                return _calls.Count;
            }
        }
    }

    public IReadOnlyList<DateTimeOffset> CallTimes
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls.Select(c => c.At)];
            }
        }
    }

    /// <summary>The messages each call was sent, in call order.</summary>
    public IReadOnlyList<IReadOnlyList<ChatMessage>> Messages
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls.Select(c => c.Messages)];
            }
        }
    }

    public ChatOptions? LastOptions
    {
        get
        {
            lock (_calls)
            {
                return _calls[^1].Options;
            }
        }
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        int index;
        lock (_calls)
        {
            index = _calls.Count;
            _calls.Add((time.GetUtcNow(), options, [.. messages]));
        }

        return attempts[Math.Min(index, attempts.Length - 1)](cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("scripted", defaultModelId: "claude-opus-5-5")
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
        // Nothing to release
    }

    // The building blocks of an attempt

    public static Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> Steps(params Func<CancellationToken, Task<ChatResponseUpdate?>>[] steps) =>
        token => Play(steps, token);

    public static Func<CancellationToken, Task<ChatResponseUpdate?>> Text(string text) =>
        _ => Task.FromResult<ChatResponseUpdate?>(new ChatResponseUpdate(ChatRole.Assistant, text));

    public static Func<CancellationToken, Task<ChatResponseUpdate?>> Empty() =>
        _ => Task.FromResult<ChatResponseUpdate?>(new ChatResponseUpdate { Role = ChatRole.Assistant });

    public static Func<CancellationToken, Task<ChatResponseUpdate?>> ToolCall(string callId, string name, IDictionary<string, object?> arguments) =>
        _ => Task.FromResult<ChatResponseUpdate?>(new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent(callId, name, arguments)]));

    public static Func<CancellationToken, Task<ChatResponseUpdate?>> Usage(long input, long output) =>
        _ => Task.FromResult<ChatResponseUpdate?>(new ChatResponseUpdate
        {
            Contents = [new UsageContent(new UsageDetails { InputTokenCount = input, OutputTokenCount = output })],
        });

    public static Func<CancellationToken, Task<ChatResponseUpdate?>> Fail(int status, double? retryAfterSeconds = null) =>
        _ => throw new FakeProviderException("scripted", (HttpStatusCode)status, retryAfterSeconds is { } s ? TimeSpan.FromSeconds(s) : null);

    /// <summary>Waits on the provider's clock, honoring cancellation like an HTTP call does.</summary>
    public static Func<CancellationToken, Task<ChatResponseUpdate?>> Wait(TimeProvider time, TimeSpan delay) =>
        async token =>
        {
            await Task.Delay(delay, time, token);
            return null;
        };

    private static async IAsyncEnumerable<ChatResponseUpdate> Play(Func<CancellationToken, Task<ChatResponseUpdate?>>[] steps, [EnumeratorCancellation] CancellationToken token)
    {
        foreach (var step in steps)
        {
            // Like reading the next chunk of an HTTP stream: a cancelled call ends there
            token.ThrowIfCancellationRequested();
            if (await step(token) is { } update)
            {
                yield return update;
            }
        }
    }
}

/// <summary>
/// Runs a call on a fake clock. Time moves only while the call waits on a timer: once it has gone quiet, the clock jumps
/// to the next pending timer, so how fast the machine runs changes nothing in fake time.
/// </summary>
internal static class FakeClock
{
    private const int QuietRounds = 5;
    private static readonly TimeSpan RealTimeLimit = TimeSpan.FromSeconds(60);

    public static async Task<T> RunAsync<T>(this TrackedFakeTime time, Task<T> task)
    {
        var started = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            await WaitUntilQuietAsync(time, task);
            if (task.IsCompleted)
            {
                break;
            }

            if (started.Elapsed > RealTimeLimit)
            {
                throw new TimeoutException("The call is neither done nor waiting on a timer.");
            }

            if (time.NextDue is { } due)
            {
                var wait = due - time.GetUtcNow();
                time.Advance(wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
            }
        }

        return await task;
    }

    public static async Task RunAsync(this TrackedFakeTime time, Task task) =>
        await time.RunAsync(task.ContinueWith(t => t, TaskScheduler.Default)).Unwrap();

    /// <summary>Reads the stream to the end on the fake clock; <paramref name="received"/> gets every update, even when it fails.</summary>
    public static async Task ReadAllAsync(this TrackedFakeTime time, IAsyncEnumerable<ChatResponseUpdate> stream, List<ChatResponseUpdate> received)
    {
        await using var updates = stream.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        while (await time.RunAsync(updates.MoveNextAsync().AsTask()))
        {
            received.Add(updates.Current);
        }
    }

    /// <summary>Waits until no timer has been created, changed or disposed for a few real milliseconds and no work is queued.</summary>
    private static async Task WaitUntilQuietAsync(TrackedFakeTime time, Task task)
    {
        var seen = time.Changes;
        var quiet = 0;
        while (quiet < QuietRounds && !task.IsCompleted)
        {
            await Task.WhenAny(task, Task.Delay(1, TestContext.Current.CancellationToken));
            var now = time.Changes;
            if (now != seen || time.CallbackPending || ThreadPool.PendingWorkItemCount > 0)
            {
                seen = now;
                quiet = 0;
            }
            else
            {
                quiet++;
            }
        }
    }
}
