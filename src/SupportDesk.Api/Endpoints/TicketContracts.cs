using SupportDesk.Core.Tickets;

namespace SupportDesk.Api.Endpoints;

/// <summary>Body of <c>POST /api/tickets</c>. Fields are nullable so a missing field reaches validation instead of failing binding.</summary>
public sealed record CreateTicketRequest(string? Subject, string? Body, string? CustomerName, string? OrderNumber);

public sealed record TicketListItemResponse(
    int Id,
    string Number,
    string Subject,
    TicketStatus Status,
    Category? Category,
    Priority? Priority,
    ClassificationStatus ClassificationStatus,
    DateTimeOffset CreatedAt);

public sealed record TicketDetailResponse(
    int Id,
    string Number,
    string Subject,
    string Body,
    string CustomerName,
    string? OrderNumber,
    TicketStatus Status,
    DateTimeOffset CreatedAt,
    TicketSummaryResponse? Summary,
    TicketClassificationResponse Classification);

public sealed record TicketSummaryResponse(string Text, string PromptVersion, DateTimeOffset GeneratedAt);

public sealed record TicketClassificationResponse(
    ClassificationStatus Status,
    Category? Category,
    Priority? Priority,
    string? Reason,
    string? PromptVersion,
    DateTimeOffset? ClassifiedAt);

internal static class TicketMapping
{
    public static TicketListItemResponse ToListItem(this Ticket ticket) =>
        new(
            ticket.Id,
            ticket.Number,
            ticket.Subject,
            ticket.Status,
            ticket.Classification.Category,
            ticket.Classification.Priority,
            ticket.Classification.Status,
            ticket.CreatedAt);

    public static TicketDetailResponse ToDetail(this Ticket ticket) =>
        new(
            ticket.Id,
            ticket.Number,
            ticket.Subject,
            ticket.Body,
            ticket.CustomerName,
            ticket.OrderNumber,
            ticket.Status,
            ticket.CreatedAt,
            ticket.Summary is { } summary ? new TicketSummaryResponse(summary.Text, summary.PromptVersion, summary.GeneratedAt) : null,
            ticket.Classification.ToResponse());

    public static TicketClassificationResponse ToResponse(this TicketClassification classification) =>
        new(
            classification.Status,
            classification.Category,
            classification.Priority,
            classification.Reason,
            classification.PromptVersion,
            classification.ClassifiedAt);
}
