using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupportDesk.Core.Health;

namespace SupportDesk.Data;

internal sealed class EfDatabaseProbe(SupportDeskDbContext db, IOptions<DatabaseOptions> options) : IDatabaseProbe
{
    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.HealthCheckTimeoutSeconds));
        try
        {
            return await db.Database.CanConnectAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own timeout fired, not the caller's cancellation
            return false;
        }
    }
}
