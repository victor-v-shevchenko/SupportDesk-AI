using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using SupportDesk.Api.Endpoints;
using SupportDesk.Core.Health;

namespace SupportDesk.UnitTests.Endpoints;

public class HealthEndpointsTests
{
    [Fact]
    public async Task Health_is_ok_when_database_is_reachable()
    {
        var result = await HealthEndpoints.GetHealth(new StubProbe(canConnect: true), TestContext.Current.CancellationToken);

        var ok = Assert.IsType<Ok<HealthResponse>>(result.Result);
        Assert.Equal(new HealthResponse("ok", "ok"), ok.Value);
    }

    [Fact]
    public async Task Health_is_503_when_database_is_unreachable()
    {
        var result = await HealthEndpoints.GetHealth(new StubProbe(canConnect: false), TestContext.Current.CancellationToken);

        var json = Assert.IsType<JsonHttpResult<HealthResponse>>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, json.StatusCode);
        Assert.Equal("unavailable", json.Value?.Database);
    }

    private sealed class StubProbe(bool canConnect) : IDatabaseProbe
    {
        public Task<bool> CanConnectAsync(CancellationToken cancellationToken) => Task.FromResult(canConnect);
    }
}
