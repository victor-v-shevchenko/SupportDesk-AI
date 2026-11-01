using SupportDesk.Core.FollowUps;

namespace SupportDesk.Api;

/// <summary>
/// Marks pending proposals older than 24 hours as expired, every 10 minutes (PLAN.md 10). Confirming checks the time
/// itself, so this only keeps the stored status current between runs. A failed run is logged and the next one tries again.
/// </summary>
public sealed partial class ProposalExpiryService(IServiceScopeFactory scopes, TimeProvider time, ILogger<ProposalExpiryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FollowUpRules.ExpiryCheckInterval, time);
        try
        {
            do
            {
                await ExpireAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down
        }
    }

    /// <summary>One run; public so tests can run it without waiting for the timer.</summary>
    public async Task ExpireAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var expired = await scope.ServiceProvider.GetRequiredService<IFollowUpRepository>().ExpireAsync(cancellationToken);
            if (expired > 0)
            {
                LogExpired(expired);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailed(exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Marked {Count} follow-up proposals as expired")]
    private partial void LogExpired(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The follow-up proposal expiry check failed; the next run tries again")]
    private partial void LogFailed(Exception exception);
}
