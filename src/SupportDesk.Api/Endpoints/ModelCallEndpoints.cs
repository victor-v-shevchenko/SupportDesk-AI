using Microsoft.AspNetCore.Http.HttpResults;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Usage;

namespace SupportDesk.Api.Endpoints;

public static class ModelCallEndpoints
{
    /// <summary>The most rows one request returns; enough for a ticket, and keeps an unfiltered request small.</summary>
    public const int MaxRows = 200;

    public static IEndpointRouteBuilder MapModelCallEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/model-calls", ListModelCalls).WithName("ListModelCalls");
        return app;
    }

    /// <summary>Usage and cost rows, newest first, for one ticket when <paramref name="ticketId"/> is given.</summary>
    public static async Task<Ok<List<ModelCallResponse>>> ListModelCalls(int? ticketId, IModelCallRepository calls, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calls);

        var rows = await calls.ListAsync(ticketId, MaxRows, cancellationToken);
        return TypedResults.Ok(rows.Select(ToResponse).ToList());
    }

    private static ModelCallResponse ToResponse(ModelCall call) => new(
        call.Id,
        call.Timestamp,
        call.Feature,
        call.Provider,
        call.Model,
        call.PromptVersion,
        call.InputTokens,
        call.OutputTokens,
        call.CostUsd,
        call.LatencyMs,
        call.IsEstimated,
        call.Outcome,
        call.TicketId,
        call.ReplyRunId);
}

public sealed record ModelCallResponse(
    long Id,
    DateTimeOffset Timestamp,
    AiFeature Feature,
    string Provider,
    string Model,
    string PromptVersion,
    long InputTokens,
    long OutputTokens,
    decimal? CostUsd,
    long LatencyMs,
    bool IsEstimated,
    ModelCallOutcome Outcome,
    int? TicketId,
    Guid? ReplyRunId);
