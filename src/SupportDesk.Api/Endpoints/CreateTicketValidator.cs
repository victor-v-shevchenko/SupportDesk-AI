using SupportDesk.Core.Tickets;

namespace SupportDesk.Api.Endpoints;

/// <summary>Checks a <see cref="CreateTicketRequest"/>. Error keys are the JSON field names, so the client can show each next to its field.</summary>
public static class CreateTicketValidator
{
    /// <summary>
    /// Returns the cleaned ticket, or the errors by field. Subject and customer name are trimmed; the body is kept as
    /// written but can't be only whitespace; an empty order number counts as none.
    /// </summary>
    public static (NewTicket? Ticket, Dictionary<string, string[]> Errors) Validate(CreateTicketRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var subject = request.Subject?.Trim() ?? "";
        var body = request.Body ?? "";
        var customerName = request.CustomerName?.Trim() ?? "";
        var orderNumber = string.IsNullOrWhiteSpace(request.OrderNumber) ? null : request.OrderNumber.Trim();

        if (subject.Length is 0 or > Ticket.SubjectMaxLength)
        {
            errors["subject"] = [$"Subject must be 1 to {Ticket.SubjectMaxLength} characters."];
        }

        if (string.IsNullOrWhiteSpace(body) || body.Length > Ticket.BodyMaxLength)
        {
            errors["body"] = [$"Body must be 1 to {Ticket.BodyMaxLength} characters."];
        }

        if (customerName.Length is 0 or > Ticket.CustomerNameMaxLength)
        {
            errors["customerName"] = [$"Customer name is required and can be at most {Ticket.CustomerNameMaxLength} characters."];
        }

        if (orderNumber is not null && !OrderNumber.IsValid(orderNumber))
        {
            errors["orderNumber"] = ["Order number must look like KO-123456."];
        }

        return errors.Count > 0 ? (null, errors) : (new NewTicket(subject, body, customerName, orderNumber), errors);
    }
}
