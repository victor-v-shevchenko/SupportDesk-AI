using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SupportDesk.Core.AI.Prompts;
using SupportDesk.Core.Tickets;

namespace SupportDesk.Core.Replies.Tools;

/// <summary>
/// Signs in a ticket's text that it tries to steer the model (PLAN.md 10, injection guard). A ticket with any of them gets a
/// reply run without the proposal tool; a false positive costs only that tool. A ticket with a <i>strong</i> one
/// (<see cref="FindStrong"/>: every signal but the role turn, which has documented false positives such as a pasted
/// <c>System: Android 15</c>) also has its classification held for a person (PLAN.md 9), so a false positive on a strong
/// signal costs that too. The names, never the matched text, are what the reply logs. The patterns catch the way injected
/// text is formatted, not what it means: a plain request to use a tool has no signal.
/// </summary>
public static partial class InjectionSignals
{
    /// <summary>The one signal that is not strong: ordinary tickets paste lines that start with <c>System:</c> or <c>User:</c>.</summary>
    public const string RoleTurnName = "role turn";

    private static readonly (string Name, Regex Pattern, bool Strong)[] Signals =
    [
        ("tool name", ToolName(), true),
        (RoleTurnName, RoleTurn(), false),
        ("tag", Tag(), true),
        ("chat template", ChatTemplate(), true),
        ("ignore instructions", IgnoreInstructions(), true),
        ("system prompt", SystemPrompt(), true),
        ("system override", SystemOverride(), true),
    ];

    /// <summary>The names of the strong signals, in the order <see cref="Find"/> reports them.</summary>
    public static IReadOnlyList<string> StrongNames { get; } = [.. Signals.Where(s => s.Strong).Select(s => s.Name)];

    /// <summary>
    /// The names of the signals found in every field the prompt shows as free text (<see cref="TicketPromptFormatter.FreeText"/>),
    /// in a fixed order; empty when there are none.
    /// </summary>
    public static IReadOnlyList<string> Find(Ticket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        var text = Normalize(string.Join('\n', TicketPromptFormatter.FreeText(ticket)));
        return [.. Signals.Where(s => s.Pattern.IsMatch(text)).Select(s => s.Name)];
    }

    /// <summary>The strong signals among those <see cref="Find"/> reports: everything but the role turn.</summary>
    public static IReadOnlyList<string> FindStrong(Ticket ticket) => [.. Find(ticket).Where(StrongNames.Contains)];

    /// <summary>
    /// Fullwidth and other compatibility forms become plain ones, invisible format characters go (zero-width spaces, and the
    /// tag characters outside the basic plane), and every line break the model reads as one becomes <c>\n</c>.
    /// </summary>
    private static string Normalize(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC);
        var plain = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (rune.Value is '\r' or '\u0085' or '\u2028' or '\u2029')
            {
                plain.Append('\n');
            }
            else if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format)
            {
                plain.Append(rune.ToString());
            }
        }

        return plain.ToString();
    }

    // Variants of the names: propose-follow-up-task and proposeFollowUpTask count. For the lookup, spaces don't, since
    // "lookup order status" is plain English
    [GeneratedRegex(@"\bpropose[\s_-]*follow[\s_-]*up[\s_-]*task\b|\blookup[_-]?order[_-]?status\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ToolName();

    // A line that starts a turn of a conversation, also behind Markdown or quote marks (**Assistant:**, > System:). Not
    // agent, tool, AI or developer: pasted chats with a human agent and parts lists use those, and they need follow-ups
    [GeneratedRegex(@"^[\s>*#-]*(assistant|system|user|human)[\s*]*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline, 1000)]
    private static partial Regex RoleTurn();

    [GeneratedRegex(@"<\s*/?\s*(system|system_prompt|instructions)\s*>|<\s*/\s*ticket\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Tag();

    // The tokens chat models are trained on, such as qwen's own
    [GeneratedRegex(@"<\|im_(start|end)\|>|\[/?INST\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ChatTemplate();

    // Not after "don't", "do not" or "never": "don't forget the earlier instruction" is a customer repeating a request
    [GeneratedRegex(@"(?<!(n['’]t|\bnot|\bnever)\s+)\b(ignore|disregard|forget)\s+(all\s+)?(the\s+|your\s+|any\s+)?(previous|prior|above|earlier)\s+instructions?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex IgnoreInstructions();

    [GeneratedRegex(@"\bsystem\s+prompt\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SystemPrompt();

    [GeneratedRegex(@"\bsystem\s+override\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SystemOverride();
}
