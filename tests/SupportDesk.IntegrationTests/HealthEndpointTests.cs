using System.Net;
using System.Net.Http.Json;
using SupportDesk.Api.Endpoints;

namespace SupportDesk.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public class HealthEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Health_returns_200_with_database_ok_in_development()
    {
        // Development applies the migrations at startup, so this also covers that path
        await using var factory = new ApiFactory(postgres.ConnectionString, "Development");
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/health", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(new HealthResponse("ok", "ok"), body);
    }

    [Fact]
    public async Task Health_returns_503_when_database_is_unreachable()
    {
        // Nothing listens on port 1; not Development, so startup doesn't try to migrate
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none", "Testing");
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/health", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>(TestContext.Current.CancellationToken);
        Assert.Equal("unavailable", body?.Database);
    }
}
