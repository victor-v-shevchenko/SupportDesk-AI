using Microsoft.AspNetCore.Http.HttpResults;
using SupportDesk.Core.AI;

namespace SupportDesk.Api.Endpoints;

/// <summary>
/// Problem details for the failures a model call can end with (PLAN.md 9). The texts are ours: an exception's message can
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

    /// <summary>The AI endpoints' rate limit (PLAN.md 5); the caller sets <c>Retry-After</c>.</summary>
    public static ProblemHttpResult RateLimited() =>
        Problem(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many requests", "Too many AI requests in a short time. Try again in a moment.");

    /// <summary>The daily spending limit (PLAN.md 6.4); the caller sets <c>Retry-After</c> to the next UTC midnight.</summary>
    public static ProblemHttpResult DailyLimitReached() =>
        Problem(StatusCodes.Status429TooManyRequests, "daily_limit", "Daily spending limit reached", "The model's spending limit for today has been reached. It resets at midnight UTC.");

    public static ProblemHttpResult InvalidOutput() =>
        Problem(StatusCodes.Status502BadGateway, "invalid_output", "The model's answer was unusable", "The model's answer was empty or unusable, so nothing was saved. Try again.");

    private static ProblemHttpResult Problem(int status, string code, string title, string detail) =>
        TypedResults.Problem(statusCode: status, title: title, detail: detail, extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = code });
}
