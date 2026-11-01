using Microsoft.AspNetCore.Http.HttpResults;
using SupportDesk.Core.AI;
using SupportDesk.Core.Summaries;

namespace SupportDesk.Api.Endpoints;

public static class SummaryEndpoints
{
    public static IEndpointRouteBuilder MapSummaryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/tickets/{id:int}/summary", GenerateSummary).WithName("GenerateSummary");
        return app;
    }

    /// <summary>Generates and saves the summary, replacing any earlier one. A failure leaves the saved summary as it was.</summary>
    public static async Task<Results<Ok<TicketSummaryResponse>, ProblemHttpResult>> GenerateSummary(
        int id, ISummaryService summaries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(summaries);

        try
        {
            var summary = await summaries.GenerateAsync(id, cancellationToken);
            return summary is null
                ? TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Ticket not found", detail: $"There is no ticket with ID {id}.")
                : TypedResults.Ok(summary.ToResponse());
        }
        catch (ModelUnavailableException exception)
        {
            return ModelProblems.Unavailable(exception);
        }
        catch (ModelRefusedException)
        {
            return ModelProblems.Refused();
        }
        catch (BudgetExceededException)
        {
            return ModelProblems.BudgetExceeded();
        }
        catch (ModelOutputException)
        {
            return ModelProblems.InvalidOutput();
        }
    }
}
