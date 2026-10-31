namespace SupportDesk.Core.Tickets;

/// <summary>A validated ticket to create; the store assigns the number, status and creation time.</summary>
public sealed record NewTicket(string Subject, string Body, string CustomerName, string? OrderNumber);
