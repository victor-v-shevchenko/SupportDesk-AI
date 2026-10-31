using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SupportDesk.Core.Health;

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

        services.AddDbContext<SupportDeskDbContext>((provider, builder) =>
            builder.UseNpgsql(provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));

        services.AddScoped<IDatabaseProbe, EfDatabaseProbe>();
        return services;
    }

    public static async Task MigrateSupportDeskDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SupportDeskDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }
}
