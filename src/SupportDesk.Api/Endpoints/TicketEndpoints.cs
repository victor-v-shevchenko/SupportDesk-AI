using Microsoft.AspNetCore.Http.HttpResults;
using SupportDesk.Core.Tickets;

namespace SupportDesk.Api.Endpoints;

public static class TicketEndpoints
{
    public static IEndpointRouteBuilder MapTicketEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tickets");
        group.MapGet("/", ListTickets).WithName("ListTickets");
        group.MapGet("/{id:int}", GetTicket).WithName("GetTicket");
        group.MapPost("/", CreateTicket).WithName("CreateTicket");
        return app;
    }

    public static async Task<Results<Ok<List<TicketListItemResponse>>, ValidationProblem>> ListTickets(
        string? status, ITicketRepository tickets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tickets);

        TicketStatus? filter = null;
        if (status is not null)
        {
            if (!TryParseStatus(status, out var parsed))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["status"] = [$"Status must be one of: {string.Join(", ", Enum.GetNames<TicketStatus>())}."],
                });
            }

            filter = parsed;
        }

        var list = await tickets.ListAsync(filter, cancellationToken);
        return TypedResults.Ok(list.Select(t => t.ToListItem()).ToList());
    }

    public static async Task<Results<Ok<TicketDetailResponse>, ProblemHttpResult>> GetTicket(
        int id, ITicketRepository tickets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tickets);

        var ticket = await tickets.FindAsync(id, cancellationToken);
        return ticket is null
            ? TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Ticket not found", detail: $"There is no ticket with ID {id}.")
            : TypedResults.Ok(ticket.ToDetail());
    }

    public static async Task<Results<Created<TicketDetailResponse>, ValidationProblem>> CreateTicket(
        CreateTicketRequest request, ITicketRepository tickets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tickets);

        var (ticket, errors) = CreateTicketValidator.Validate(request);
        if (ticket is null)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var created = await tickets.CreateAsync(ticket, cancellationToken);
        return TypedResults.Created($"/api/tickets/{created.Id}", created.ToDetail());
    }

    /// <summary>Accepts the status names in any case, but not numbers, which <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> would allow.</summary>
    public static bool TryParseStatus(string value, out TicketStatus status)
    {
        var name = Enum.GetNames<TicketStatus>().FirstOrDefault(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase));
        status = name is null ? default : Enum.Parse<TicketStatus>(name);
        return name is not null;
    }
}
