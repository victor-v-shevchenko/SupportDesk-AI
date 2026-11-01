using Microsoft.AspNetCore.Http.HttpResults;
using SupportDesk.Core.AI;

namespace SupportDesk.Api.Endpoints;

/// <summary>
/// The problem details for a model call that failed (PLAN.md 5, 6.5), shared by the non-streaming AI endpoints:
/// <c>503</c> when the model is unavailable or timed out, <c>422</c> when it declined, <c>413</c> when the call is over the
/// token budget. The messages are fixed; nothing from the provider's error reaches the client.
/// </summary>
public static class ModelErrorProblems
{
    /// <summary>The problem for <paramref name="exception"/>, or null when it isn't a model error.</summary>
    public static ProblemHttpResult? From(Exception exception) => exception switch
    {
        ModelUnavailableException { IsTimeout: true } => Problem(
            StatusCodes.Status503ServiceUnavailable, "The AI model timed out", "The model took too long to answer. Try again in a moment."),
        ModelUnavailableException => Problem(
            StatusCodes.Status503ServiceUnavailable, "The AI model is unavailable", "The model provider couldn't be reached or returned an error. Try again in a moment."),
        ModelRefusedException => Problem(
            StatusCodes.Status422UnprocessableEntity, "The AI model declined", "The model declined to answer this request."),
        BudgetExceededException => Problem(
            StatusCodes.Status413PayloadTooLarge, "Over the token budget", "This ticket is too long for the model's token budget."),
        _ => null,
    };

    private static ProblemHttpResult Problem(int status, string title, string detail) =>
        TypedResults.Problem(statusCode: status, title: title, detail: detail);
}
