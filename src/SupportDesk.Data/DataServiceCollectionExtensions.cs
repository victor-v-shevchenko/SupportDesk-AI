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

    /// <summary>Any fixed value; PostgreSQL advisory locks are keyed by a 64-bit number agreed between the callers.</summary>
    private const long StartupLockKey = 0x5355505044534B;

    /// <summary>
    /// Applies the migrations, then loads <c>seed/</c> (see <see cref="SupportDeskSeeder"/>). Holds a PostgreSQL advisory
    /// lock throughout, so two instances starting against one database run this one after the other instead of racing.
    /// </summary>
    public static async Task MigrateAndSeedSupportDeskDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var connectionString = scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString;

        // Its own connection, so the lock outlives the connections EF opens and closes
        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken);
        await ExecuteAsync(lockConnection, "SELECT pg_advisory_lock(@key)", cancellationToken);
        try
        {
            await scope.ServiceProvider.GetRequiredService<SupportDeskDbContext>().Database.MigrateAsync(cancellationToken);
            await scope.ServiceProvider.GetRequiredService<SupportDeskSeeder>().SeedAsync(cancellationToken);
        }
        finally
        {
            // A pooled connection keeps session locks when it goes back to the pool, so release it explicitly
            await ExecuteAsync(lockConnection, "SELECT pg_advisory_unlock(@key)", CancellationToken.None);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("key", StartupLockKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
