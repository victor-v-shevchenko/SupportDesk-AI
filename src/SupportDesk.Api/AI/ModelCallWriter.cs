using SupportDesk.Core.AI.Usage;

namespace SupportDesk.Api.AI;

/// <summary>
/// Moves rows from <see cref="ModelCallBuffer"/> to the database in batches, off the request path (PLAN.md 6.6). A batch
/// that fails to save is logged and dropped, so a database outage can't back up into the pipeline. On shutdown it
/// saves what's left, for a few seconds at most.
/// </summary>
public sealed partial class ModelCallWriter(ModelCallBuffer queue, IServiceScopeFactory scopes, ILogger<ModelCallWriter> logger) : BackgroundService
{
    private const int BatchSize = 100;
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await queue.Reader.WaitToReadAsync(stoppingToken))
            {
                await SaveBatchAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; drain below
        }

        queue.Complete();
        using var drain = new CancellationTokenSource(DrainTimeout);
        while (queue.Reader.Count > 0 && !drain.IsCancellationRequested)
        {
            await SaveBatchAsync(drain.Token);
        }
    }

    private async Task SaveBatchAsync(CancellationToken cancellationToken)
    {
        var batch = new List<ModelCall>(BatchSize);
        while (batch.Count < BatchSize && queue.Reader.TryRead(out var call))
        {
            batch.Add(call);
        }

        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IModelCallRepository>().AddRangeAsync(batch, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogSaveFailed(exception, batch.Count);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't save {Count} model call rows; they are dropped")]
    private partial void LogSaveFailed(Exception exception, int count);
}
