using Microsoft.AspNetCore.Http.HttpResults;
using SupportDesk.Core.AI;

namespace SupportDesk.Api.Endpoints;

/// <summary>
/// Problem details for the failures a model call can end with (PLAN.md 6.5). The texts are ours: an exception's message can
/// name limits or come from a provider SDK, so it never reaches the client. <c>code</c> uses the reply stream's error codes (PLAN.md 7).
/// </summary>
public static class ModelProblems
{
    public static ProblemHttpResult Unavailable(ModelUnavailableException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.IsTimeout
            ? Problem(StatusCodes.Status503ServiceUnavailable, "timeout", "The model timed out", "The model took too long to answer. Try again in a moment.")
            : Problem(StatusCodes.Status503ServiceUnavailable, "model_unavailable", "The model is unavailable", "The model provider couldn't be reached or returned an error. Try again in a moment.");
    }

    public static ProblemHttpResult Refused() =>
        Problem(StatusCodes.Status422UnprocessableEntity, "model_refused", "The model declined the request", "The model declined to answer for this ticket.");

    public static ProblemHttpResult BudgetExceeded() =>
        Problem(StatusCodes.Status413PayloadTooLarge, "budget_exceeded", "Over the token budget", "This ticket is too long for the model's token budget.");

    public static ProblemHttpResult InvalidOutput() =>
        Problem(StatusCodes.Status502BadGateway, "invalid_output", "The model's answer was unusable", "The model returned an empty answer. Try again.");

    private static ProblemHttpResult Problem(int status, string code, string title, string detail) =>
        TypedResults.Problem(statusCode: status, title: title, detail: detail, extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = code });
}
