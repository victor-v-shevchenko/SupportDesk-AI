using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace SupportDesk.IntegrationTests;

/// <summary>Runs the API in memory against the given connection string.</summary>
public sealed class ApiFactory(string connectionString, string environment) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        // Added last, so it wins over the dev container's ConnectionStrings__SupportDesk variable
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SupportDesk"] = connectionString,
        }));
    }
}
