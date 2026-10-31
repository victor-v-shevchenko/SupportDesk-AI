namespace SupportDesk.Core.Health;

/// <summary>Checks whether the database can be reached.</summary>
public interface IDatabaseProbe
{
    Task<bool> CanConnectAsync(CancellationToken cancellationToken);
}
