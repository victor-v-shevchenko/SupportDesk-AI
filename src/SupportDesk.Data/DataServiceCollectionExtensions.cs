using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using SupportDesk.Core.Articles;
using SupportDesk.Core.Health;
using SupportDesk.Core.Tickets;
using SupportDesk.Data.Repositories;
using SupportDesk.Data.Seeding;

namespace SupportDesk.Data;

public static class DataServiceCollectionExtensions
{
    public const string ConnectionStringName = "SupportDesk";

    /// <summary>Any fixed value; PostgreSQL advisory locks are keyed by a 64-bit number agreed between the callers.</summary>
    private const long StartupLockKey = 0x5355505044534B;

    public static IServiceCollection AddSupportDeskData(this IServiceCollection services)
    {
        services.AddOptions<DatabaseOptions>()
            .BindConfiguration("Database")
            .Configure<IConfiguration>((options, configuration) =>
                options.ConnectionString = configuration.GetConnectionString(ConnectionStringName) ?? "")
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<SeedOptions>()
            .BindConfiguration("Seed")
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddDbContext<SupportDeskDbContext>((provider, builder) =>
            builder.UseNpgsql(provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IDatabaseProbe, EfDatabaseProbe>();
        services.AddScoped<ITicketRepository, TicketRepository>();
        services.AddScoped<IArticleRepository, ArticleRepository>();
        services.AddScoped<SupportDeskSeeder>();
        return services;
    }

    /// <summary>
    /// Applies the migrations, then loads <c>seed/</c> (see <see cref="SupportDeskSeeder"/>). Holds a PostgreSQL advisory
    /// lock throughout, so two instances starting against one database run this one after the other instead of racing.
    /// </summary>
    public static async Task MigrateAndSeedSupportDeskDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var connectionString = scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString;

        // Its own connection, so the lock outlives the connections EF opens and closes. Closing it releases the lock too
        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken);
        // No command timeout: waiting for another instance's first migration can take longer than the default 30 seconds
        await ExecuteAsync(lockConnection, "SELECT pg_advisory_lock(@key)", commandTimeoutSeconds: 0, cancellationToken);
        try
        {
            await scope.ServiceProvider.GetRequiredService<SupportDeskDbContext>().Database.MigrateAsync(cancellationToken);
            await scope.ServiceProvider.GetRequiredService<SupportDeskSeeder>().SeedAsync(cancellationToken);
        }
        finally
        {
            try
            {
                await ExecuteAsync(lockConnection, "SELECT pg_advisory_unlock(@key)", commandTimeoutSeconds: 30, CancellationToken.None);
            }
            catch (NpgsqlException)
            {
                // The connection is broken, which already ended the session and its lock; keep the original exception
            }
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = commandTimeoutSeconds };
        command.Parameters.AddWithValue("key", StartupLockKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
