using System.Globalization;
using System.Text.RegularExpressions;
using SupportDesk.Core.Retrieval;

namespace SupportDesk.Core.Replies;

/// <summary>
/// Finds the citation markers in a reply and checks them against the numbered sources (PLAN.md 8). A marker is a number in
/// square brackets, with optional spaces (<c>[2]</c>, <c>[ 3 ]</c>), or a comma-separated list (<c>[1, 2]</c>); markers
/// can touch each other (<c>[1][2]</c>). The web client's parser (<c>citations.ts</c>) reads the same forms.
/// </summary>
public static partial class CitationParser
{
    /// <summary>Every marker number in <paramref name="text"/>, in order, repeats included.</summary>
    public static IReadOnlyList<int> Markers(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return [.. MarkerRegex().Matches(text)
            .SelectMany(m => m.Groups["n"].Captures)
            .Select(c => int.Parse(c.Value, NumberStyles.None, CultureInfo.InvariantCulture))];
    }

    /// <summary>
    /// Markers <c>1…N</c> (N is the number of sources) map to source <c>n</c>'s article: one citation per distinct marker, in
    /// marker order. Numbers outside <c>1…N</c> go into <see cref="ReplyCitations.Removed"/>, once each, in order.
    /// </summary>
    public static ReplyCitations Check(string text, IReadOnlyList<RetrievedChunk> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var markers = Markers(text).Distinct().Order().ToList();
        var citations = markers
            .Where(n => n >= 1 && n <= sources.Count)
            .Select(n => new Citation(n, sources[n - 1].ArticleSlug, sources[n - 1].ArticleTitle))
            .ToList();
        var removed = markers.Where(n => n < 1 || n > sources.Count).ToList();
        return new ReplyCitations(citations, removed);
    }

    // ASCII digits only (\d also matches other scripts), at most 9, so a number always fits in an int; a longer one isn't a marker and stays plain text
    [GeneratedRegex(@"\[\s*(?<n>[0-9]{1,9})(?:\s*,\s*(?<n>[0-9]{1,9}))*\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerRegex();
}
