using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SupportDesk.IntegrationTests;

/// <summary>
/// Runs the API in memory against the given connection string, with any extra settings on top. The fake chat and
/// embedding providers are the default, since startup embeds the articles and CI has no model; a test can override them.
/// <paramref name="services"/> replaces registrations, for example the embedding generator.
/// </summary>
public sealed class ApiFactory(
    string connectionString,
    string environment,
    IReadOnlyDictionary<string, string?>? settings = null,
    ILoggerProvider? logger = null,
    Action<IServiceCollection>? services = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var values = new Dictionary<string, string?>
        {
            ["AI:Chat:Provider"] = "Fake",
            ["AI:Embeddings:Provider"] = "Fake",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        values["ConnectionStrings:SupportDesk"] = connectionString;

        builder.UseEnvironment(environment);
        if (logger is not null)
        {
            builder.ConfigureLogging(logging => logging.AddProvider(logger));
        }

        // Added last, so it wins over the dev container's ConnectionStrings__SupportDesk variable
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(values));
        if (services is not null)
        {
            builder.ConfigureTestServices(services);
        }
    }
}
