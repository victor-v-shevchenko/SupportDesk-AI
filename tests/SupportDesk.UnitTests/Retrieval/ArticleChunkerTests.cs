using System.Security.Cryptography;
using System.Text;
using SupportDesk.Core.Retrieval;
using SupportDesk.Data.Seeding;

namespace SupportDesk.UnitTests.Retrieval;

public class ArticleChunkerTests
{
    // Seven paragraphs of 296 characters: four fit in 1,200, so the section is split after the fourth
    private static readonly string[] LongParagraphs = [.. Enumerable.Range(1, 7).Select(Paragraph)];

    private static readonly string Article =
        "Intro before any heading.\n\n" +
        "## Short section\nOne short paragraph.\n\n### A subheading\nStays in its section.\n\n" +
        "## Long section\n" + string.Join("\n\n", LongParagraphs) + "\n\n" +
        "## Empty section\n\n" +
        "## Last\nThe end.\n";

    [Fact]
    public void Text_before_the_first_heading_is_a_chunk_without_a_heading()
    {
        var chunks = ArticleChunker.Chunk(Article);

        Assert.Null(chunks[0].Heading);
        Assert.Equal("Intro before any heading.", chunks[0].Text);
        Assert.Equal(chunks[0].Text, chunks[0].EmbeddingInput);
    }

    [Fact]
    public void A_short_section_is_one_chunk_with_its_heading_and_keeps_deeper_headings()
    {
        var chunk = ArticleChunker.Chunk(Article)[1];

        Assert.Equal("Short section", chunk.Heading);
        Assert.Equal("One short paragraph.\n\n### A subheading\nStays in its section.", chunk.Text);
        Assert.Equal("Short section\n" + chunk.Text, chunk.EmbeddingInput);
    }

    [Fact]
    public void A_long_section_is_split_on_paragraphs_with_overlap_and_keeps_its_heading()
    {
        var pieces = ArticleChunker.Chunk(Article).Where(c => c.Heading == "Long section").ToList();

        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, p => Assert.True(p.Text.Length <= ArticleChunker.MaxLength, $"{p.Text.Length} characters"));
        // The first piece ends on a paragraph boundary
        Assert.EndsWith(LongParagraphs[3], pieces[0].Text, StringComparison.Ordinal);
        // The second starts with the end of the first, at most 150 characters from a word boundary, then the next paragraph
        var overlap = pieces[1].Text[..pieces[1].Text.IndexOf("\n\n", StringComparison.Ordinal)];
        Assert.InRange(overlap.Length, 100, ArticleChunker.OverlapLength);
        Assert.EndsWith(overlap, pieces[0].Text, StringComparison.Ordinal);
        Assert.StartsWith(overlap + "\n\n" + LongParagraphs[4], pieces[1].Text, StringComparison.Ordinal);
        // Nothing is lost
        Assert.All(LongParagraphs, p => Assert.Contains(pieces, piece => piece.Text.Contains(p, StringComparison.Ordinal)));
    }

    [Fact]
    public void Empty_sections_are_dropped_and_ordinals_count_up_from_zero()
    {
        var chunks = ArticleChunker.Chunk(Article);

        Assert.Equal([null, "Short section", "Long section", "Long section", "Last"], chunks.Select(c => c.Heading));
        Assert.Equal([0, 1, 2, 3, 4], chunks.Select(c => c.Ordinal));
    }

    [Fact]
    public void A_paragraph_longer_than_a_chunk_is_cut_between_words()
    {
        var words = string.Join(' ', Enumerable.Range(0, 600).Select(i => "word" + i));

        var chunks = ArticleChunker.Chunk("## Wall of text\n" + words);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= ArticleChunker.MaxLength, $"{c.Text.Length} characters"));
        Assert.All(chunks, c => Assert.Equal("Wall of text", c.Heading));
        Assert.All(chunks, c => Assert.DoesNotContain(c.Text.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries), w => !w.StartsWith("word", StringComparison.Ordinal)));
        Assert.EndsWith("word599", chunks[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_content_hash_is_sha256_of_the_embedded_text()
    {
        var chunk = ArticleChunker.Chunk(Article)[1];

        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("Short section\n" + chunk.Text)));
        Assert.Equal(expected, chunk.ContentHash);
    }

    [Fact]
    public void Windows_line_endings_give_the_same_chunks()
    {
        Assert.Equal(ArticleChunker.Chunk(Article), ArticleChunker.Chunk(Article.ReplaceLineEndings("\r\n")));
    }

    [Fact]
    public async Task Every_seed_article_section_is_chunked_within_the_limit()
    {
        var articles = await SeedFiles.ReadArticlesAsync(Path.Combine(AppContext.BaseDirectory, "seed"), TestContext.Current.CancellationToken);

        foreach (var article in articles)
        {
            var chunks = ArticleChunker.Chunk(article.BodyMarkdown);
            var sections = article.BodyMarkdown.Split('\n').Count(l => l.StartsWith("## ", StringComparison.Ordinal));
            // At least one chunk per section (returns-policy has a section long enough to be split)
            Assert.True(chunks.Count >= sections, $"{article.Slug}: {chunks.Count} chunks for {sections} sections");
            Assert.All(chunks, c => Assert.InRange(c.Text.Length, 1, ArticleChunker.MaxLength));
        }
    }

    private static string Paragraph(int number) =>
        $"Paragraph {number} " + string.Join(' ', Enumerable.Repeat("explains one part of the returns process in some detail.", 5));
}
