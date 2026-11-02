using Microsoft.Extensions.Time.Testing;

namespace SupportDesk.UnitTests.AI;

/// <summary>
/// A fake clock that knows which timers are pending, so a test can move time to the next one when the code under test
/// has gone quiet (see <see cref="FakeClock"/>) instead of moving it on a real-time cadence. Its timers can also fire late
/// in real time, like on a busy machine where the code waiting on a timer is slow to run.
/// </summary>
internal sealed class TrackedFakeTime : FakeTimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<TrackedTimer> _timers = [];
    private readonly List<TimeSpan> _created = [];
    private readonly List<(TimeSpan DueTime, TaskCompletionSource Started)> _watches = [];
    private long _changes;
    private int _inFlight;
    private Exception? _callbackFailure;

    public TrackedFakeTime(DateTimeOffset? start = null, TimeSpan? callbackLatency = null)
        : base(start ?? new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero))
    {
        CallbackLatency = callbackLatency ?? TimeSpan.Zero;
    }

    /// <summary>Real time between a timer coming due and its callback running.</summary>
    public TimeSpan CallbackLatency { get; }

    /// <summary>Counts timers created, changed and disposed; it stays the same while the code under test is quiet.</summary>
    public long Changes => Interlocked.Read(ref _changes);

    /// <summary>True while a due timer's callback is waiting to run (only with <see cref="CallbackLatency"/>) or running.</summary>
    public bool CallbackPending => Volatile.Read(ref _inFlight) > 0;

    /// <summary>What a late timer callback threw, so the test fails with it instead of waiting for the call to time out.</summary>
    public Exception? CallbackFailure => Volatile.Read(ref _callbackFailure);

    /// <summary>When the earliest pending timer is due, or null when none is.</summary>
    public DateTimeOffset? NextDue
    {
        get
        {
            lock (_gate)
            {
                return _timers.Select(t => t.Due).Where(d => d is not null).Min();
            }
        }
    }

    /// <summary>
    /// Completes when a timer is created, or already was, with exactly <paramref name="dueTime"/> as its first wait, so a
    /// test can act at that moment without sleeping in real time. <c>Task.Delay</c> cuts its wait to whole milliseconds, so
    /// <paramref name="dueTime"/> must be whole milliseconds too.
    /// </summary>
    public Task TimerStartedAsync(TimeSpan dueTime)
    {
        lock (_gate)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_created.Contains(dueTime))
            {
                started.SetResult();
            }
            else
            {
                _watches.Add((dueTime, started));
            }

            return started.Task;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new TrackedTimer(this, callback, period);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        // The base class may fire a timer that is already due from inside this call
        timer.Attach(base.CreateTimer(timer.Fire, state, dueTime, period), dueTime);
        lock (_gate)
        {
            _created.Add(dueTime);
            foreach (var watch in _watches.Where(w => w.DueTime == dueTime))
            {
                watch.Started.TrySetResult();
            }
        }

        return timer;
    }

    private void Touch() => Interlocked.Increment(ref _changes);

    private sealed class TrackedTimer(TrackedFakeTime owner, TimerCallback callback, TimeSpan period) : ITimer
    {
        private ITimer? _inner;
        private TimeSpan _period = period;

        public DateTimeOffset? Due { get; private set; }

        public void Attach(ITimer inner, TimeSpan dueTime)
        {
            _inner = inner;
            SetDue(dueTime);
            owner.Touch();
        }

        public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
        {
            lock (owner._gate)
            {
                _period = newPeriod;
            }

            var changed = _inner!.Change(dueTime, newPeriod);
            SetDue(dueTime);
            owner.Touch();
            return changed;
        }

        public void Fire(object? state)
        {
            // A one-shot timer is spent once it is due, even if its callback runs late; a periodic one is next due a period
            // from now (also when it fired inside CreateTimer, before it had a due time)
            lock (owner._gate)
            {
                Due = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : owner.GetUtcNow() + _period;
            }

            Interlocked.Increment(ref owner._inFlight);
            if (owner.CallbackLatency == TimeSpan.Zero)
            {
                RunCallback(state);
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(owner.CallbackLatency);
                        RunCallback(state);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.CompareExchange(ref owner._callbackFailure, ex, null);
                    }
                });
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                Due = null;
                owner._timers.Remove(this);
            }

            _inner?.Dispose();
            owner.Touch();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private void SetDue(TimeSpan dueTime)
        {
            lock (owner._gate)
            {
                // A timer set to zero is already due: it fires inside the call
                Due = dueTime == Timeout.InfiniteTimeSpan || dueTime <= TimeSpan.Zero ? null : owner.GetUtcNow() + dueTime;
            }
        }

        private void RunCallback(object? state)
        {
            try
            {
                callback(state);
            }
            finally
            {
                Interlocked.Decrement(ref owner._inFlight);
            }
        }
    }
}
