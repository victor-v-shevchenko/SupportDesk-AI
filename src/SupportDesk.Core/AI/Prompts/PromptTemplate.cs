using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace SupportDesk.Core.AI.Prompts;

/// <summary>
/// One prompt file (PLAN.md 6.3): a <c>## System</c> section and a <c>## User</c> template with <c>{{placeholders}}</c>,
/// optionally followed by a <c>## Retry</c> template, the user message a feature sends after rejecting the model's answer.
/// Text before <c>## System</c> is a note for people and isn't sent. The system section can't have placeholders, so user
/// text can only ever reach the user messages.
/// </summary>
public sealed partial class PromptTemplate
{
    private const string SystemHeading = "## System";
    private const string UserHeading = "## User";
    private const string RetryHeading = "## Retry";

    private PromptTemplate(string version, string system, string user, string? retry)
    {
        Version = version;
        System = system;
        User = user;
        Placeholders = FindPlaceholders(user);
        Retry = retry;
        RetryPlaceholders = retry is null ? new HashSet<string>() : FindPlaceholders(retry);
    }

    /// <summary>The file name without <c>.md</c>, for example <c>reply.v1</c>; stored with every result and model call.</summary>
    public string Version { get; }

    public string System { get; }

    public string User { get; }

    public IReadOnlySet<string> Placeholders { get; }

    /// <summary>The <c>## Retry</c> template, or null when the file has none.</summary>
    public string? Retry { get; }

    public IReadOnlySet<string> RetryPlaceholders { get; }

    public static PromptTemplate Parse(string version, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var system = FindHeading(lines, SystemHeading, version) ?? throw NotOne(SystemHeading, 0, version);
        var user = FindHeading(lines, UserHeading, version) ?? throw NotOne(UserHeading, 0, version);
        var retry = FindHeading(lines, RetryHeading, version);
        if (user < system)
        {
            throw new InvalidDataException($"{version}: '{SystemHeading}' must come before '{UserHeading}'.");
        }

        if (retry < user)
        {
            throw new InvalidDataException($"{version}: '{RetryHeading}' must come after '{UserHeading}'.");
        }

        var systemText = Section(lines, system + 1, user, SystemHeading, version);
        var userText = Section(lines, user + 1, retry ?? lines.Length, UserHeading, version);
        var retryText = retry is { } start ? Section(lines, start + 1, lines.Length, RetryHeading, version) : null;
        if (PlaceholderRegex().IsMatch(systemText))
        {
            throw new InvalidDataException($"{version}: the system section can't have placeholders; user text goes in the user section only.");
        }

        return new PromptTemplate(version, systemText, userText, retryText);
    }

    /// <summary>
    /// Fills the user template in one pass, so a value that contains <c>{{name}}</c> is left as it is. Every placeholder needs
    /// a value and every value a placeholder, so a typo fails instead of sending a half-filled prompt.
    /// </summary>
    public string RenderUser(IReadOnlyDictionary<string, string> values) => Render(User, Placeholders, values);

    /// <summary>Fills the <c>## Retry</c> template the same way as <see cref="RenderUser"/>.</summary>
    public string RenderRetry(IReadOnlyDictionary<string, string> values) =>
        Retry is null
            ? throw new InvalidOperationException($"{Version} has no '{RetryHeading}' section.")
            : Render(Retry, RetryPlaceholders, values);

    /// <summary>The system message and the filled user message.</summary>
    public IReadOnlyList<ChatMessage> ToMessages(IReadOnlyDictionary<string, string> values) =>
        [new ChatMessage(ChatRole.System, System), new ChatMessage(ChatRole.User, RenderUser(values))];

    private string Render(string template, IReadOnlySet<string> placeholders, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var missing = placeholders.Where(p => !values.ContainsKey(p)).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException($"{Version}: no value for {string.Join(", ", missing)}.", nameof(values));
        }

        var unknown = values.Keys.Where(k => !placeholders.Contains(k)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"{Version}: no placeholder for {string.Join(", ", unknown)}.", nameof(values));
        }

        return PlaceholderRegex().Replace(template, m => values[m.Groups["name"].Value]);
    }

    /// <summary>The line of <paramref name="heading"/>, or null when there's none; more than one is an error.</summary>
    private static int? FindHeading(string[] lines, string heading, string version)
    {
        var matches = lines.Select((line, index) => (line, index)).Where(l => l.line.TrimEnd() == heading).ToList();
        return matches.Count switch
        {
            0 => null,
            1 => matches[0].index,
            _ => throw NotOne(heading, matches.Count, version),
        };
    }

    private static InvalidDataException NotOne(string heading, int count, string version) =>
        new($"{version}: expected one '{heading}' line, found {count}.");

    private static string Section(string[] lines, int start, int end, string heading, string version)
    {
        var text = string.Join('\n', lines[start..end]).Trim();
        return text.Length > 0 ? text : throw new InvalidDataException($"{version}: the '{heading}' section is empty.");
    }

    private static HashSet<string> FindPlaceholders(string template) =>
        PlaceholderRegex().Matches(template).Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\{\{(?<name>[a-zA-Z][a-zA-Z0-9_]*)\}\}")]
    private static partial Regex PlaceholderRegex();
}
