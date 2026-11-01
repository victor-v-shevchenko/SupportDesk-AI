using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SupportDesk.Core.Tickets;

namespace SupportDesk.Core.AI.Prompts;

/// <summary>
/// Puts a ticket into a prompt as customer data (PLAN.md 6.3): inside <c>&lt;ticket&gt;…&lt;/ticket&gt;</c>, with every field
/// escaped so nothing the customer wrote can close the tag or open a new one.
/// </summary>
public static partial class TicketPromptFormatter
{
    public static string Format(Ticket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        var text = new StringBuilder();
        text.Append("<ticket>\n");
        text.Append(CultureInfo.InvariantCulture, $"Number: {Escape(ticket.Number)}\n");
        text.Append(CultureInfo.InvariantCulture, $"Customer: {Escape(ticket.CustomerName)}\n");
        if (ticket.OrderNumber is { } orderNumber)
        {
            text.Append(CultureInfo.InvariantCulture, $"Order number: {Escape(orderNumber)}\n");
        }

        text.Append(CultureInfo.InvariantCulture, $"Subject: {Escape(ticket.Subject)}\n");
        text.Append(CultureInfo.InvariantCulture, $"Body:\n{Escape(ticket.Body)}\n");
        text.Append("</ticket>");
        return text.ToString();
    }

    /// <summary>
    /// Replaces the <c>&lt;</c> of anything that reads as a <c>ticket</c> tag (<c>&lt;/ticket&gt;</c>, <c>&lt;/TICKET &gt;</c>,
    /// <c>&lt; /ticket</c>, <c>&lt;ticket&gt;</c>) with <c>&amp;lt;</c>, and the same for <c>tool_result</c>, so the customer
    /// can't fake a tool result the reply prompt says to trust as the store's data. The rest of the text is left as the
    /// customer wrote it.
    /// </summary>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return TicketTagRegex().Replace(text, "&lt;");
    }

    [GeneratedRegex(@"<(?=\s*/?\s*(?:ticket|tool_result))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TicketTagRegex();
}
