namespace SupportDesk.Core.Tickets;

public sealed class Ticket
{
    public const int SubjectMaxLength = 200;
    public const int BodyMaxLength = 5000;
    public const int CustomerNameMaxLength = 200;

    public int Id { get; set; }

    /// <summary>Display number, for example <c>T-1001</c>.</summary>
    public required string Number { get; set; }

    public required string Subject { get; set; }

    public required string Body { get; set; }

    public required string CustomerName { get; set; }

    public string? OrderNumber { get; set; }

    public TicketStatus Status { get; set; } = TicketStatus.Open;

    public DateTimeOffset CreatedAt { get; set; }

    public TicketSummary? Summary { get; set; }

    public TicketClassification Classification { get; set; } = new();
}
