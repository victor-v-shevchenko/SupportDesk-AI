using System.Text.RegularExpressions;
using SupportDesk.Core.Tickets;

namespace SupportDesk.Core.Replies.Tools;

/// <summary>
/// Signs in a ticket's text that it tries to steer the model (PLAN.md 10, injection guard). A ticket with any of them gets a
/// reply run without the proposal tool. The names, never the matched text, are what the reply logs.
/// </summary>
public static partial class InjectionSignals
{
    private static readonly (string Name, Regex Pattern)[] Signals =
    [
        ("tool name", ToolName()),
        ("role turn", RoleTurn()),
        ("tag", Tag()),
        ("ignore instructions", IgnoreInstructions()),
        ("system prompt", SystemPrompt()),
        ("system override", SystemOverride()),
    ];

    /// <summary>The names of the signals found in the ticket's subject and body, in a fixed order; empty when there are none.</summary>
    public static IReadOnlyList<string> Find(Ticket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        var text = $"{ticket.Subject}\n{ticket.Body}";
        return [.. Signals.Where(s => s.Pattern.IsMatch(text)).Select(s => s.Name)];
    }

    [GeneratedRegex(@"\b(propose_follow_up_task|lookup_order_status)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ToolName();

    // A line that starts a turn of a conversation, as a ticket faking one would write it
    [GeneratedRegex(@"^\s*(assistant|system|user)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline, 1000)]
    private static partial Regex RoleTurn();

    [GeneratedRegex(@"<\s*/?\s*system\s*>|<\s*/\s*ticket\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Tag();

    [GeneratedRegex(@"\bignore\s+(all\s+)?(the\s+)?(previous|prior|above)\s+instructions\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex IgnoreInstructions();

    [GeneratedRegex(@"\bsystem\s+prompt\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SystemPrompt();

    [GeneratedRegex(@"\bsystem\s+override\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SystemOverride();
}
