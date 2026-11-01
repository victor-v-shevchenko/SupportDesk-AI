using Microsoft.AspNetCore.Http.HttpResults;
using SupportDesk.Core.FollowUps;

namespace SupportDesk.Api.Endpoints;

public sealed record FollowUpsResponse(IReadOnlyList<FollowUpProposalResponse> Proposals, IReadOnlyList<FollowUpTaskResponse> Tasks);

public sealed record FollowUpProposalResponse(
    Guid Id,
    Guid ReplyRunId,
    string Title,
    string Notes,
    DateOnly? DueDate,
    ProposalStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed record FollowUpTaskResponse(int Id, Guid ProposalId, string Title, string Notes, DateOnly? DueDate, DateTimeOffset CreatedAt);

public static class FollowUpEndpoints
{
    public static IEndpointRouteBuilder MapFollowUpEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/tickets/{id:int}/follow-ups", ListFollowUps).WithName("ListFollowUps");
        app.MapPost("/api/follow-ups/{proposalId:guid}/confirm", ConfirmProposal).WithName("ConfirmProposal");
        return app;
    }

    /// <summary>The ticket's proposals and tasks, newest first (PLAN.md 10).</summary>
    public static async Task<Results<Ok<FollowUpsResponse>, ProblemHttpResult>> ListFollowUps(
        int id, IFollowUpRepository followUps, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(followUps);

        var list = await followUps.ListAsync(id, cancellationToken);
        return list is null
            ? TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Ticket not found", detail: $"There is no ticket with ID {id}.")
            : TypedResults.Ok(new FollowUpsResponse([.. list.Proposals.Select(ToResponse)], [.. list.Tasks.Select(ToResponse)]));
    }

    /// <summary>
    /// The only way a task is created: the user confirms a proposal. Confirming again returns the same task; an expired
    /// proposal is a <c>409</c>.
    /// </summary>
    public static async Task<Results<Ok<FollowUpTaskResponse>, ProblemHttpResult>> ConfirmProposal(
        Guid proposalId, IFollowUpRepository followUps, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(followUps);

        var result = await followUps.ConfirmAsync(proposalId, cancellationToken);
        return result switch
        {
            { Outcome: ConfirmOutcome.Confirmed, Task: { } task } => TypedResults.Ok(ToResponse(task)),
            { Outcome: ConfirmOutcome.Expired } => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict, title: "Proposal expired", detail: "This proposal is more than 24 hours old and can no longer be confirmed."),
            _ => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Proposal not found", detail: $"There is no proposal with ID {proposalId}."),
        };
    }

    private static FollowUpProposalResponse ToResponse(FollowUpProposal p) =>
        new(p.Id, p.ReplyRunId, p.Title, p.Notes, p.DueDate, p.Status, p.CreatedAt, p.ExpiresAt);

    private static FollowUpTaskResponse ToResponse(FollowUpTask t) => new(t.Id, t.ProposalId, t.Title, t.Notes, t.DueDate, t.CreatedAt);
}
