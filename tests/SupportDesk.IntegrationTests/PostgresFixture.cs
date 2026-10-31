using Npgsql;
using Testcontainers.PostgreSql;

namespace SupportDesk.IntegrationTests;

/// <summary>One PostgreSQL + pgvector container for the whole test run; Testcontainers generates its credentials.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>Creates an empty database in the shared container, so a test that writes data doesn't affect the others.</summary>
    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var name = "test_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollectionDefinition : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
