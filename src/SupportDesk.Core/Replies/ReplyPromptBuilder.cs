using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Prompts;
using SupportDesk.Core.Replies.Tools;
using SupportDesk.Core.Retrieval;
using SupportDesk.Core.Tickets;

namespace SupportDesk.Core.Replies;

/// <summary>The messages of a reply call and the sources they number <c>[1]</c>…<c>[N]</c>, in that order.</summary>
public sealed record ReplyPrompt(IReadOnlyList<ChatMessage> Messages, IReadOnlyList<RetrievedChunk> Sources, int Dropped);

/// <summary>The prompt builder of PLAN.md 6.4: the retrieval query, the numbered sources and the trim to the token budget.</summary>
public static partial class ReplyPromptBuilder
{
    /// <summary>The longest retrieval query; embedding models only read so much (<c>mxbai-embed-large</c>: 512 tokens).</summary>
    public const int QueryMaxLength = 1000;

    /// <summary>What <c>{{sources}}</c> holds when no article is similar enough, so the model knows it has nothing to cite.</summary>
    public const string NoSources = "(No articles matched this ticket.)";

    /// <summary>The ticket's subject and body, cut at a word boundary to <see cref="QueryMaxLength"/> characters.</summary>
    public static string BuildQuery(Ticket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        var query = $"{ticket.Subject.Trim()}\n\n{ticket.Body.Trim()}".Trim();
        if (query.Length <= QueryMaxLength)
        {
            return query;
        }

        var cut = query.LastIndexOfAny([' ', '\n', '\t'], QueryMaxLength);
        return query[..(cut > 0 ? cut : QueryMaxLength)].TrimEnd();
    }

    /// <summary>
    /// Adds chunks in score order while the call's input estimate (<see cref="TokenEstimator"/>, with the system prompt, the
    /// ticket and any tools in <paramref name="options"/>) stays within <paramref name="maxInputTokens"/>. The first chunk
    /// that doesn't fit and every one after it are dropped, so the lowest scores go first. If the prompt is over the limit
    /// even without sources, it's returned anyway and the budget guard refuses the call.
    /// </summary>
    public static ReplyPrompt Build(PromptTemplate template, Ticket ticket, IReadOnlyList<RetrievedChunk> chunks, ChatOptions? options, int maxInputTokens)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(chunks);

        var ranked = chunks.OrderByDescending(c => c.Similarity).ToList();
        var ticketText = TicketPromptFormatter.Format(ticket);
        var system = SystemFor(template, options);

        var count = 0;
        var messages = Render(template, system, ticketText, []);
        while (count < ranked.Count)
        {
            var next = Render(template, system, ticketText, ranked[..(count + 1)]);
            if (TokenEstimator.EstimateInput(next, options) > maxInputTokens)
            {
                break;
            }

            messages = next;
            count++;
        }

        return new ReplyPrompt(messages, ranked[..count], ranked.Count - count);
    }

    /// <summary>
    /// <c>[n] &lt;title&gt; — &lt;heading&gt;</c> (or just the title), then the text; a blank line between sources (PLAN.md 6.3).
    /// Article text is data too, so anything in it that reads as a <c>ticket</c>, <c>sources</c> or <c>tool_result</c> tag is escaped.
    /// </summary>
    public static string FormatSources(IReadOnlyList<RetrievedChunk> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        if (sources.Count == 0)
        {
            return NoSources;
        }

        var text = new StringBuilder();
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            if (i > 0)
            {
                text.Append("\n\n");
            }

            text.Append(CultureInfo.InvariantCulture, $"[{i + 1}] {Escape(source.ArticleTitle)}");
            if (!string.IsNullOrWhiteSpace(source.Heading))
            {
                text.Append(CultureInfo.InvariantCulture, $" — {Escape(source.Heading)}");
            }

            text.Append('\n').Append(Escape(source.Text));
        }

        return text.ToString();
    }

    /// <summary>Replaces the <c>&lt;</c> of anything that reads as a <c>ticket</c>, <c>sources</c> or <c>tool_result</c> tag with <c>&amp;lt;</c>.</summary>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return PromptTagRegex().Replace(text, "&lt;");
    }

    /// <summary>
    /// The system prompt without the instruction line (<c>- &lt;tool&gt;: …</c>) of every reply tool that <paramref name="options"/>
    /// doesn't offer, so a run that withholds the proposal tool (the injection guard, PLAN.md 10) never tells the model about it.
    /// </summary>
    internal static string SystemFor(PromptTemplate template, ChatOptions? options)
    {
        var offered = (options?.Tools ?? []).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var withheld = ReplyTools.Names
            .Where(name => !offered.Contains(name))
            .Select(name => $"- {name}:")
            .ToList();
        if (withheld.Count == 0)
        {
            return template.System;
        }

        var lines = template.System.ReplaceLineEndings("\n").Split('\n')
            .Where(line => !withheld.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal)));
        return string.Join('\n', lines);
    }

    private static List<ChatMessage> Render(PromptTemplate template, string system, string ticketText, IReadOnlyList<RetrievedChunk> sources)
    {
        var messages = template.ToMessages(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sources"] = FormatSources(sources),
            ["ticket"] = ticketText,
        });
        return [new ChatMessage(ChatRole.System, system), messages[1]];
    }

    [GeneratedRegex(@"<(?=\s*/?\s*(?:ticket|sources|tool_result))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PromptTagRegex();
}
