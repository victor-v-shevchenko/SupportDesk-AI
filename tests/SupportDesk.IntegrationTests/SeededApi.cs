using Microsoft.EntityFrameworkCore;
using SupportDesk.Data;

namespace SupportDesk.IntegrationTests;

/// <summary>The API in Development (migrated and seeded at startup) against its own new database.</summary>
public sealed class SeededApi : IAsyncDisposable
{
    private SeededApi(string connectionString, IReadOnlyDictionary<string, string?>? settings)
    {
        ConnectionString = connectionString;
        Factory = new ApiFactory(connectionString, "Development", settings);
        Client = Factory.CreateClient();
    }

    public string ConnectionString { get; }

    public ApiFactory Factory { get; }

    public HttpClient Client { get; }

    public static async Task<SeededApi> StartAsync(PostgresFixture postgres, CancellationToken cancellationToken, IReadOnlyDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        return new SeededApi(await postgres.CreateDatabaseAsync(cancellationToken), settings);
    }

    /// <summary>A DbContext on this API's database, for arranging and checking data directly.</summary>
    public SupportDeskDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SupportDeskDbContext>().UseNpgsql(ConnectionString).Options);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
    }
}

/// <summary>The seed folder the build copies next to the test assembly; the API reads the same files.</summary>
public static class SeedPath
{
    public static string Value { get; } = Path.Combine(AppContext.BaseDirectory, "seed");
}
