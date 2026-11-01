using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Data;

namespace SupportDesk.IntegrationTests;

/// <summary>The API in Development (migrated and seeded at startup) against its own new database.</summary>
public sealed class SeededApi : IAsyncDisposable
{
    private SeededApi(string connectionString, IReadOnlyDictionary<string, string?>? settings, Action<IServiceCollection>? services)
    {
        ConnectionString = connectionString;
        Factory = new ApiFactory(connectionString, "Development", settings, services: services);
        Client = Factory.CreateClient();
    }

    public string ConnectionString { get; }

    public ApiFactory Factory { get; }

    public HttpClient Client { get; }

    public static async Task<SeededApi> StartAsync(
        PostgresFixture postgres,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? services = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        return new SeededApi(await postgres.CreateDatabaseAsync(cancellationToken), settings, services);
    }

    /// <summary>A DbContext on this API's database, for arranging and checking data directly.</summary>
    public SupportDeskDbContext CreateDbContext() => TestDatabase.CreateDbContext(ConnectionString);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
    }
}

/// <summary>A DbContext configured like the app's (pgvector included), for tests that work on a database directly.</summary>
public static class TestDatabase
{
    public static SupportDeskDbContext CreateDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<SupportDeskDbContext>().UseNpgsql(connectionString, npgsql => npgsql.UseVector()).Options);
}

/// <summary>The seed folder the build copies next to the test assembly; the API reads the same files.</summary>
public static class SeedPath
{
    public static string Value { get; } = Path.Combine(AppContext.BaseDirectory, "seed");
}
