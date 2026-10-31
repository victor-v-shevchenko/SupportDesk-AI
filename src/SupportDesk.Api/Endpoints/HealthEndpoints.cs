using Microsoft.AspNetCore.Http.HttpResults;
using SupportDesk.Core.Health;

namespace SupportDesk.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", GetHealth).WithName("GetHealth");
        return app;
    }

    public static async Task<Results<Ok<HealthResponse>, JsonHttpResult<HealthResponse>>> GetHealth(
        IDatabaseProbe database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        if (await database.CanConnectAsync(cancellationToken))
        {
            return TypedResults.Ok(new HealthResponse("ok", "ok"));
        }

        return TypedResults.Json(new HealthResponse("degraded", "unavailable"), statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    // Deliberate Sonar issue for the step 1 test pull request (S1144, unused private member). Never merged.
    private static string UnusedHelper() => "unused";
}

public sealed record HealthResponse(string Status, string Database);
