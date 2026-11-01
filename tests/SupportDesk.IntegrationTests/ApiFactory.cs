using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SupportDesk.IntegrationTests;

/// <summary>Runs the API in memory against the given connection string, with any extra settings on top.</summary>
public sealed class ApiFactory(
    string connectionString, string environment, IReadOnlyDictionary<string, string?>? settings = null, ILoggerProvider? logger = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        if (logger is not null)
        {
            builder.ConfigureLogging(logging => logging.AddProvider(logger));
        }

        // Added last, so it wins over the dev container's ConnectionStrings__SupportDesk variable
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>(settings ?? new Dictionary<string, string?>())
        {
            ["ConnectionStrings:SupportDesk"] = connectionString,
        }));
    }
}
