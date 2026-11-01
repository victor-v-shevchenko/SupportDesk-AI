namespace SupportDesk.Core.Summaries;

/// <summary>The output check for a summary (PLAN.md 9): plain text of at most <see cref="MaxLength"/> characters.</summary>
public static class SummaryOutput
{
    public const int MaxLength = 600;

    /// <summary>Characters that may follow a sentence's final punctuation and still belong to it: closing quotes and brackets.</summary>
    private const string Closers = "\"'”’)]";

    /// <summary>
    /// Trims <paramref name="text"/> and, when it's longer than <see cref="MaxLength"/>, cuts it after the last full sentence
    /// that fits. A sentence ends with <c>.</c>, <c>!</c> or <c>?</c> (and any closing quotes or brackets) followed by
    /// whitespace or the end of the text, so <c>1.5</c> or <c>example.com</c> aren't sentence ends. When not even the first
    /// sentence fits, it's cut at the last space and ends with <c>…</c>. Returns null for empty or whitespace-only output,
    /// which counts as a failure.
    /// </summary>
    public static string? Check(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.Length <= MaxLength)
        {
            return trimmed;
        }

        var end = LastSentenceEnd(trimmed);
        return end > 0 ? trimmed[..end] : CutAtWord(trimmed);
    }

    /// <summary>The length of the longest prefix of at most <see cref="MaxLength"/> characters that ends a sentence, or 0.</summary>
    private static int LastSentenceEnd(string text)
    {
        // A boundary at i means text[..i] is whole sentences: text[i] is whitespace. Only called when text is longer than MaxLength
        for (var i = MaxLength; i > 0; i--)
        {
            if (char.IsWhiteSpace(text[i]) && EndsSentence(text, i))
            {
                return i;
            }
        }

        return 0;
    }

    private static bool EndsSentence(string text, int end)
    {
        var j = end - 1;
        while (j >= 0 && Closers.Contains(text[j], StringComparison.Ordinal))
        {
            j--;
        }

        return j >= 0 && text[j] is '.' or '!' or '?';
    }

    private static string CutAtWord(string text)
    {
        // Leave room for the ellipsis
        var limit = MaxLength - 1;
        var space = text.LastIndexOf(' ', limit);
        var cut = space > 0 ? text[..space] : text[..limit];
        return cut.TrimEnd() + "…";
    }
}
