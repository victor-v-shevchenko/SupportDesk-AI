using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SupportDesk.Api.AI;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Usage;
using SupportDesk.UnitTests.Telemetry;

namespace SupportDesk.UnitTests.AI;

/// <summary>The background writer between the usage buffer and the database (PLAN.md 6.6), against a scripted repository.</summary>
public sealed class ModelCallWriterTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ScriptedRepository _repository = new();
    private readonly TestMetrics _metrics = new();
    private readonly ModelCallBuffer _buffer;
    private readonly ServiceProvider _services;
    private readonly LoggerFactory _loggers;
    private readonly ModelCallWriter _writer;

    public ModelCallWriterTests()
    {
        _buffer = new(Options.Create(new AiOptions()), _metrics.Metrics, NullLogger<ModelCallBuffer>.Instance);
        _services = new ServiceCollection().AddSingleton<IModelCallRepository>(_repository).BuildServiceProvider();
        _loggers = new LoggerFactory([_logs]);
        _writer = new ModelCallWriter(_buffer, _services.GetRequiredService<IServiceScopeFactory>(), _metrics.Metrics, _loggers.CreateLogger<ModelCallWriter>(), _time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _writer.Dispose();
        _metrics.Dispose();
        _loggers.Dispose();
        _services.Dispose();
        _logs.Dispose();
    }

    [Fact]
    public async Task Rows_are_saved_in_batches_of_at_most_100()
    {
        Enqueue(250);

        await _writer.StartAsync(Ct);
        await _repository.RowsSaved(250);
        await _writer.StopAsync(Ct);

        Assert.Equal([100, 100, 50], _repository.BatchSizes);
    }

    [Fact]
    public async Task A_failed_batch_is_logged_and_the_next_one_still_saves()
    {
        _repository.FailNext = true;
        Enqueue(150);

        await _writer.StartAsync(Ct);
        await _repository.RowsSaved(50);
        await _writer.StopAsync(Ct);

        Assert.Equal([50], _repository.SavedBatchSizes);
        Assert.Contains(_logs.Messages, m => m.Contains("Couldn't save 100 model call rows", StringComparison.Ordinal));
        Assert.Equal(100, TestMetrics.Sum(_metrics.Dropped, ("reason", "save_failed")));
        Assert.Single(_metrics.Dropped.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task Stopping_saves_the_rows_still_queued()
    {
        _repository.HoldFirstSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(150);
        await _writer.StartAsync(Ct);
        await _repository.FirstSaveStarted;

        var stopping = _writer.StopAsync(Ct);
        _repository.HoldFirstSave.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(150, _repository.SavedRows);
        Assert.DoesNotContain(_logs.Messages, m => m.Contains("dropped", StringComparison.Ordinal));
        Assert.Empty(_metrics.Dropped.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task A_drain_that_runs_past_its_deadline_logs_how_many_rows_it_lost()
    {
        _repository.HoldFirstSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _repository.HangSecondSave = true;
        Enqueue(150);
        await _writer.StartAsync(Ct);
        await _repository.FirstSaveStarted;

        var stopping = _writer.StopAsync(Ct);
        _repository.HoldFirstSave.SetResult();
        await _repository.SecondSaveStarted;
        _time.Advance(TimeSpan.FromSeconds(5));
        await stopping.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Contains(_logs.Messages, m => m.Contains("Shutdown interrupted saving 50 model call rows", StringComparison.Ordinal));
        Assert.Equal(50, TestMetrics.Sum(_metrics.Dropped, ("reason", "shutdown")));
        Assert.Equal(100, _repository.SavedRows);
    }

    [Fact]
    public async Task A_drain_cut_off_mid_save_counts_the_rows_still_queued_behind_it_as_lost()
    {
        _repository.HoldFirstSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _repository.HangSecondSave = true;
        Enqueue(250);
        await _writer.StartAsync(Ct);
        await _repository.FirstSaveStarted;

        var stopping = _writer.StopAsync(Ct);
        _repository.HoldFirstSave.SetResult();
        await _repository.SecondSaveStarted;
        _time.Advance(TimeSpan.FromSeconds(5));
        await stopping.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // The batch in flight (100) and the 50 behind it; the 100 saved first are not lost
        Assert.Contains(_logs.Messages, m => m.Contains("Shutdown interrupted saving 150 model call rows", StringComparison.Ordinal));
        Assert.Equal(150, TestMetrics.Sum(_metrics.Dropped, ("reason", "shutdown")));
        Assert.Equal(100, _repository.SavedRows);
    }

    [Fact]
    public async Task A_drain_whose_deadline_passes_between_batches_logs_the_rows_it_left_behind()
    {
        _repository.HoldFirstSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The second batch saves, but the deadline passes while it does, so the loop ends with 50 rows queued
        _repository.DuringSecondSave = () => _time.Advance(TimeSpan.FromSeconds(5));
        Enqueue(250);
        await _writer.StartAsync(Ct);
        await _repository.FirstSaveStarted;

        var stopping = _writer.StopAsync(Ct);
        _repository.HoldFirstSave.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(200, _repository.SavedRows);
        Assert.Contains(_logs.Messages, m => m.Contains("50 model call rows", StringComparison.Ordinal) && m.Contains("dropped", StringComparison.Ordinal));
        Assert.Equal(50, TestMetrics.Sum(_metrics.Dropped, ("reason", "drain_deadline")));
    }

    private void Enqueue(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _buffer.Add(new ModelCall { Feature = AiFeature.Reply, Provider = "fake", Model = "fake", PromptVersion = "reply.v1" });
        }
    }

    private sealed class ScriptedRepository : IModelCallRepository
    {
        private readonly Lock _gate = new();
        private readonly List<int> _batches = [];
        private readonly List<int> _saved = [];
        private readonly TaskCompletionSource _firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<(int Rows, TaskCompletionSource Reached)> _waiters = [];
        private int _calls;

        public bool FailNext { get; set; }

        public TaskCompletionSource? HoldFirstSave { get; set; }

        public bool HangSecondSave { get; set; }

        /// <summary>Runs inside the second save, before it completes.</summary>
        public Action? DuringSecondSave { get; set; }

        public Task FirstSaveStarted => _firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        public Task SecondSaveStarted => _secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        /// <summary>The size of every batch the writer handed over, whether or not it saved.</summary>
        public IReadOnlyList<int> BatchSizes
        {
            get
            {
                lock (_gate)
                {
                    return [.. _batches];
                }
            }
        }

        public IReadOnlyList<int> SavedBatchSizes
        {
            get
            {
                lock (_gate)
                {
                    return [.. _saved];
                }
            }
        }

        public int SavedRows => SavedBatchSizes.Sum();

        /// <summary>Completes when at least <paramref name="rows"/> rows have been saved.</summary>
        public Task RowsSaved(int rows)
        {
            lock (_gate)
            {
                if (_saved.Sum() >= rows)
                {
                    return Task.CompletedTask;
                }

                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((rows, reached));
                return reached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }
        }

        public async Task AddRangeAsync(IReadOnlyList<ModelCall> calls, CancellationToken cancellationToken)
        {
            int call;
            lock (_gate)
            {
                call = ++_calls;
                _batches.Add(calls.Count);
            }

            if (call == 1)
            {
                _firstStarted.TrySetResult();
                if (FailNext)
                {
                    throw new InvalidOperationException("The database is down");
                }

                if (HoldFirstSave is { } hold)
                {
                    await hold.Task;
                }
            }
            else if (call == 2)
            {
                _secondStarted.TrySetResult();
                DuringSecondSave?.Invoke();
                if (HangSecondSave)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
            }

            lock (_gate)
            {
                _saved.Add(calls.Count);
                var total = _saved.Sum();
                foreach (var waiter in _waiters.Where(w => total >= w.Rows))
                {
                    waiter.Reached.TrySetResult();
                }
            }
        }

        public Task<IReadOnlyList<ModelCall>> ListAsync(int? ticketId, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
