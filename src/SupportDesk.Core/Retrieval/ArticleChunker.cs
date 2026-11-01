using System.Security.Cryptography;
using System.Text;

namespace SupportDesk.Core.Retrieval;

/// <summary>
/// Splits an article's Markdown into chunks for embedding (PLAN.md 8): one per <c>##</c> section, with the text before the
/// first heading as a section without a heading. A section longer than <see cref="MaxLength"/> is split on paragraph
/// boundaries, and each piece after the first starts with the last <see cref="OverlapLength"/> characters of the one before.
/// </summary>
public static class ArticleChunker
{
    public const int MaxLength = 1200;
    public const int OverlapLength = 150;

    private const string HeadingPrefix = "## ";
    private const string ParagraphSeparator = "\n\n";

    public static IReadOnlyList<ArticleChunkDraft> Chunk(string bodyMarkdown)
    {
        ArgumentNullException.ThrowIfNull(bodyMarkdown);

        var chunks = new List<ArticleChunkDraft>();
        foreach (var (heading, text) in Sections(bodyMarkdown.ReplaceLineEndings("\n")))
        {
            foreach (var piece in Split(text))
            {
                chunks.Add(new ArticleChunkDraft(chunks.Count, heading, piece));
            }
        }

        return chunks;
    }

    /// <summary>The <c>##</c> sections in order; <c>###</c> and deeper stay inside their section. Empty sections are dropped.</summary>
    private static IEnumerable<(string? Heading, string Text)> Sections(string markdown)
    {
        string? heading = null;
        var text = new StringBuilder();
        foreach (var line in markdown.Split('\n'))
        {
            if (line.StartsWith(HeadingPrefix, StringComparison.Ordinal))
            {
                if (text.ToString().Trim() is { Length: > 0 } done)
                {
                    yield return (heading, done);
                }

                heading = line[HeadingPrefix.Length..].Trim();
                text.Clear();
            }
            else
            {
                text.Append(line).Append('\n');
            }
        }

        if (text.ToString().Trim() is { Length: > 0 } last)
        {
            yield return (heading, last);
        }
    }

    private static List<string> Split(string text)
    {
        if (text.Length <= MaxLength)
        {
            return [text];
        }

        // Room left for the paragraphs once the overlap and its separator are in front of them
        const int budget = MaxLength - OverlapLength - 2;
        var paragraphs = text.Split(ParagraphSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(p => p.Length <= budget ? [p] : SplitOnWords(p, budget));

        var pieces = new List<string>();
        var current = new StringBuilder();
        foreach (var paragraph in paragraphs)
        {
            var separator = current.Length == 0 ? 0 : ParagraphSeparator.Length;
            if (current.Length + separator + paragraph.Length > MaxLength)
            {
                var done = current.ToString();
                pieces.Add(done);
                current.Clear().Append(Overlap(done)).Append(ParagraphSeparator);
            }
            else if (separator > 0)
            {
                current.Append(ParagraphSeparator);
            }

            current.Append(paragraph);
        }

        pieces.Add(current.ToString());
        return pieces;
    }

    /// <summary>The end of a piece, at most <see cref="OverlapLength"/> characters, starting at a word.</summary>
    private static string Overlap(string piece)
    {
        if (piece.Length <= OverlapLength)
        {
            return piece;
        }

        var start = piece.Length - OverlapLength;
        var space = piece.IndexOfAny([' ', '\n'], start);
        return (space < 0 ? piece[start..] : piece[(space + 1)..]).Trim();
    }

    /// <summary>A paragraph too long to fit a chunk, cut at the last space before each limit (or at the limit, for a very long word).</summary>
    private static IEnumerable<string> SplitOnWords(string paragraph, int limit)
    {
        var rest = paragraph;
        while (rest.Length > limit)
        {
            var cut = rest.LastIndexOf(' ', limit);
            if (cut <= 0)
            {
                cut = limit;
            }

            yield return rest[..cut].Trim();
            rest = rest[cut..].Trim();
        }

        if (rest.Length > 0)
        {
            yield return rest;
        }
    }
}

/// <summary>A chunk before it's stored. <see cref="EmbeddingInput"/> is what gets embedded, and <see cref="ContentHash"/> is its SHA-256.</summary>
public sealed record ArticleChunkDraft(int Ordinal, string? Heading, string Text)
{
    public string EmbeddingInput => Heading is null ? Text : Heading + "\n" + Text;

    public string ContentHash => HashOf(EmbeddingInput);

    /// <summary>Lowercase hex SHA-256 of the UTF-8 text; the fixtures use the same hash for chunks and questions.</summary>
    public static string HashOf(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
