using Microsoft.AspNetCore.Http.HttpResults;
using SupportDesk.Core.AI;
using SupportDesk.Core.Classification;

namespace SupportDesk.Api.Endpoints;

public static class ClassificationEndpoints
{
    public static IEndpointRouteBuilder MapClassificationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/tickets/{id:int}/classification", ClassifyTicket).WithName("ClassifyTicket");
        return app;
    }

    /// <summary>Classifies the ticket and saves the result, replacing any earlier one (PLAN.md 9).</summary>
    public static async Task<Results<Ok<TicketClassificationResponse>, ProblemHttpResult>> ClassifyTicket(
        int id, IClassificationService classifier, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(classifier);

        try
        {
            var classification = await classifier.ClassifyAsync(id, cancellationToken);
            return classification is null
                ? TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Ticket not found", detail: $"There is no ticket with ID {id}.")
                : TypedResults.Ok(classification.ToResponse());
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
    }
}
