using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
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

    public static async Task MigrateSupportDeskDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SupportDeskDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>Loads <c>seed/</c>; safe to run on every start (see <see cref="SupportDeskSeeder"/>).</summary>
    public static async Task SeedSupportDeskDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SupportDeskSeeder>().SeedAsync(cancellationToken);
    }
}
