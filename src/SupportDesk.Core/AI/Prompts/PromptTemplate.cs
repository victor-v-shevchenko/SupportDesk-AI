using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace SupportDesk.Core.AI.Prompts;

/// <summary>
/// One prompt file (PLAN.md 6.3): a <c>## System</c> section and a <c>## User</c> template with <c>{{placeholders}}</c>.
/// Text before <c>## System</c> is a note for people and isn't sent. The system section can't have placeholders, so user
/// text can only ever reach the user message.
/// </summary>
public sealed partial class PromptTemplate
{
    private const string SystemHeading = "## System";
    private const string UserHeading = "## User";

    private PromptTemplate(string version, string system, string user, IReadOnlySet<string> placeholders)
    {
        Version = version;
        System = system;
        User = user;
        Placeholders = placeholders;
    }

    /// <summary>The file name without <c>.md</c>, for example <c>reply.v1</c>; stored with every result and model call.</summary>
    public string Version { get; }

    public string System { get; }

    public string User { get; }

    public IReadOnlySet<string> Placeholders { get; }

    public static PromptTemplate Parse(string version, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var system = FindHeading(lines, SystemHeading, version);
        var user = FindHeading(lines, UserHeading, version);
        if (user < system)
        {
            throw new InvalidDataException($"{version}: '{SystemHeading}' must come before '{UserHeading}'.");
        }

        var systemText = Section(lines, system + 1, user, SystemHeading, version);
        var userText = Section(lines, user + 1, lines.Length, UserHeading, version);
        if (PlaceholderRegex().IsMatch(systemText))
        {
            throw new InvalidDataException($"{version}: the system section can't have placeholders; user text goes in the user section only.");
        }

        var placeholders = PlaceholderRegex().Matches(userText).Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
        return new PromptTemplate(version, systemText, userText, placeholders);
    }

    /// <summary>
    /// Fills the user template in one pass, so a value that contains <c>{{name}}</c> is left as it is. Every placeholder needs
    /// a value and every value a placeholder, so a typo fails instead of sending a half-filled prompt.
    /// </summary>
    public string RenderUser(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var missing = Placeholders.Where(p => !values.ContainsKey(p)).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException($"{Version}: no value for {string.Join(", ", missing)}.", nameof(values));
        }

        var unknown = values.Keys.Where(k => !Placeholders.Contains(k)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"{Version}: no placeholder for {string.Join(", ", unknown)}.", nameof(values));
        }

        return PlaceholderRegex().Replace(User, m => values[m.Groups["name"].Value]);
    }

    /// <summary>The system message and the filled user message.</summary>
    public IReadOnlyList<ChatMessage> ToMessages(IReadOnlyDictionary<string, string> values) =>
        [new ChatMessage(ChatRole.System, System), new ChatMessage(ChatRole.User, RenderUser(values))];

    private static int FindHeading(string[] lines, string heading, string version)
    {
        var matches = lines.Select((line, index) => (line, index)).Where(l => l.line.TrimEnd() == heading).ToList();
        return matches.Count == 1
            ? matches[0].index
            : throw new InvalidDataException($"{version}: expected one '{heading}' line, found {matches.Count}.");
    }

    private static string Section(string[] lines, int start, int end, string heading, string version)
    {
        var text = string.Join('\n', lines[start..end]).Trim();
        return text.Length > 0 ? text : throw new InvalidDataException($"{version}: the '{heading}' section is empty.");
    }

    [GeneratedRegex(@"\{\{(?<name>[a-zA-Z][a-zA-Z0-9_]*)\}\}")]
    private static partial Regex PlaceholderRegex();
}
