using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
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
/// "Quiet" is a short real-time check, not a proof: for about 20 rounds of 1 ms no timer was created, changed or disposed,
/// no timer callback is running, no thread-pool work is queued, and the pool has not completed any work item. A continuation
/// that is running finishes as a completed item, so a chain of pool hops towards a timer keeps the call from looking quiet.
/// The test classes using this are in <see cref="FakeClockGroup"/>, which runs alone, because the pool counters are
/// process-wide and other tests' work would hide or fake that progress. The check polls on a dedicated thread, so its own
/// waits don't move the counters. What remains is a single pool work item that runs for more than the whole window (about
/// 20 ms) without finishing before it sets a timer, such as a thread descheduled on a heavily loaded machine; the call then
/// ends early, which fails visibly (fewer attempts than expected), never as a wrong pass.
/// </summary>
internal static class FakeClock
{
    private const int QuietRounds = 20;
    private static readonly TimeSpan RealTimeLimit = TimeSpan.FromSeconds(120);

    public static async Task<T> RunAsync<T>(this TrackedFakeTime time, Task<T> task)
    {
        var sinceProgress = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            await WaitUntilQuietAsync(time, task);
            if (time.CallbackFailure is { } failure)
            {
                throw new InvalidOperationException("A timer callback threw.", failure);
            }

            if (task.IsCompleted)
            {
                break;
            }

            if (sinceProgress.Elapsed > RealTimeLimit)
            {
                throw new TimeoutException("The call has neither finished nor waited on a timer for two minutes of real time.");
            }

            if (time.NextDue is { } due)
            {
                var wait = due - time.GetUtcNow();
                var before = time.Changes;
                time.Advance(wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
                // Moving the clock, or a timer reacting, is progress; a timer that stays due without firing is not
                if (wait > TimeSpan.Zero || time.Changes != before)
                {
                    sinceProgress.Restart();
                }
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

    /// <summary>Waits until nothing has changed on the clock or in the thread pool for the whole quiet window.</summary>
    private static Task WaitUntilQuietAsync(TrackedFakeTime time, Task task) =>
        // A dedicated thread: awaiting Task.Delay here would complete pool work items itself and move the counter we read
        Task.Factory.StartNew(() => WaitUntilQuiet(time, task), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static void WaitUntilQuiet(TrackedFakeTime time, Task task)
    {
        var seen = Snapshot(time);
        var quiet = 0;
        while (quiet < QuietRounds && !task.IsCompleted)
        {
            Thread.Sleep(1);
            var now = Snapshot(time);
            if (now == seen && !time.CallbackPending && ThreadPool.PendingWorkItemCount == 0)
            {
                quiet++;
            }
            else
            {
                seen = now;
                quiet = 0;
            }
        }
    }

    private static (long Changes, long Completed) Snapshot(TrackedFakeTime time) => (time.Changes, ThreadPool.CompletedWorkItemCount);
}
