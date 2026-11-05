using System.Diagnostics;
using SupportDesk.Core.AI.Usage;
using SupportDesk.Core.Telemetry;

namespace SupportDesk.Api.AI;

/// <summary>
/// Moves rows from <see cref="ModelCallBuffer"/> to the database in batches, off the request path (PLAN.md 6.6). A batch
/// that fails to save is logged and dropped, so a database outage can't back up into the pipeline. On shutdown it
/// saves what's left, for a few seconds at most.
/// </summary>
public sealed partial class ModelCallWriter(ModelCallBuffer queue, IServiceScopeFactory scopes, ILogger<ModelCallWriter> logger, TimeProvider time) : BackgroundService
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
        using var drain = new CancellationTokenSource(DrainTimeout, time);
        while (queue.Reader.Count > 0 && !drain.IsCancellationRequested)
        {
            await SaveBatchAsync(drain.Token, draining: true);
        }

        // The deadline passed between batches
        if (queue.Reader.Count > 0)
        {
            LogDrainLeftRows(queue.Reader.Count);
        }
    }

    private async Task SaveBatchAsync(CancellationToken cancellationToken, bool draining = false)
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

        // A background service, so each batch is a trace of its own
        using var activity = SupportDeskTelemetry.Source.StartActivity("model_calls.save", ActivityKind.Internal, parentContext: default);
        activity?.SetTag("supportdesk.row_count", batch.Count);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IModelCallRepository>().AddRangeAsync(batch, cancellationToken);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            activity.RecordFailure(exception, cancellationToken);
            // Shutdown cut the save short; say so rather than losing the rows silently
            // While draining, nothing will save what is still queued behind this batch
            LogSaveCancelled(draining ? batch.Count + queue.Reader.Count : batch.Count);
            throw;
        }
        catch (Exception exception)
        {
            activity.RecordFailure(exception, cancellationToken);
            LogSaveFailed(exception, batch.Count);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shutdown interrupted saving {Count} model call rows; they are dropped")]
    private partial void LogSaveCancelled(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shutdown ran out of time saving model call rows; {Count} model call rows are dropped")]
    private partial void LogDrainLeftRows(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't save {Count} model call rows; they are dropped")]
    private partial void LogSaveFailed(Exception exception, int count);
}
