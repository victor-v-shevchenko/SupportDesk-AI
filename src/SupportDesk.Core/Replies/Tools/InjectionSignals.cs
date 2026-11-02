using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SupportDesk.Core.AI.Prompts;
using SupportDesk.Core.Tickets;

namespace SupportDesk.Core.Replies.Tools;

/// <summary>
/// Signs in a ticket's text that it tries to steer the model (PLAN.md 10, injection guard). A ticket with any of them gets a
/// reply run without the proposal tool. The names, never the matched text, are what the reply logs. The patterns catch the
/// way injected text is formatted, not what it means: a plain request to use a tool has no signal.
/// </summary>
public static partial class InjectionSignals
{
    private static readonly (string Name, Regex Pattern)[] Signals =
    [
        ("tool name", ToolName()),
        ("role turn", RoleTurn()),
        ("tag", Tag()),
        ("chat template", ChatTemplate()),
        ("ignore instructions", IgnoreInstructions()),
        ("system prompt", SystemPrompt()),
        ("system override", SystemOverride()),
    ];

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

    /// <summary>Fullwidth and other compatibility forms become plain ones, and invisible format characters (zero-width spaces) go.</summary>
    private static string Normalize(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC);
        var plain = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)
            {
                plain.Append(c);
            }
        }

        return plain.ToString();
    }

    // Underscores, spaces, hyphens or nothing between the words, so propose-follow-up-task and proposeFollowUpTask count too
    [GeneratedRegex(@"propose[\s_-]*follow[\s_-]*up[\s_-]*task|lookup[\s_-]*order[\s_-]*status", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ToolName();

    // A line that starts a turn of a conversation, also behind Markdown or quote marks (**Assistant:**, > System:)
    [GeneratedRegex(@"^[\s>*#-]*(assistant|system|user|human|ai|agent|developer|tool)[\s*]*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline, 1000)]
    private static partial Regex RoleTurn();

    [GeneratedRegex(@"<\s*/?\s*(system|system_prompt|instructions)\s*>|<\s*/\s*ticket\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Tag();

    // The tokens chat models are trained on, such as qwen's own
    [GeneratedRegex(@"<\|im_(start|end)\|>|\[/?INST\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ChatTemplate();

    [GeneratedRegex(@"\b(ignore|disregard|forget)\s+(all\s+)?(the\s+|your\s+|any\s+)?(previous|prior|above|earlier)\s+instructions?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex IgnoreInstructions();

    [GeneratedRegex(@"\bsystem\s+prompt\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SystemPrompt();

    [GeneratedRegex(@"\bsystem\s+override\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SystemOverride();
}
