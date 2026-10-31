using SupportDesk.Data.Seeding;

namespace SupportDesk.UnitTests.Seeding;

public class ArticleParserTests
{
    [Fact]
    public void Front_matter_gives_slug_and_title_and_the_rest_is_the_body()
    {
        var article = SeedFiles.ParseArticle("a.md", "---\nslug: gift-cards\ntitle: Gift cards: the basics\n---\n\nIntro.\n\n## One\nText\n");

        Assert.Equal(new SeedArticle("gift-cards", "Gift cards: the basics", "Intro.\n\n## One\nText"), article);
    }

    [Fact]
    public void Windows_line_endings_are_accepted()
    {
        var article = SeedFiles.ParseArticle("a.md", "---\r\nslug: s\r\ntitle: T\r\n---\r\nBody\r\n## H\r\n");

        Assert.Equal(new SeedArticle("s", "T", "Body\n## H"), article);
    }

    [Theory]
    [InlineData("slug: s\ntitle: T\n---\nBody", "first line")]
    [InlineData("---\nslug: s\ntitle: T\nBody", "no closing")]
    [InlineData("---\ntitle: T\n---\nBody", "'slug'")]
    [InlineData("---\nslug: s\ntitle:\n---\nBody", "'title'")]
    [InlineData("---\nslug: s\ntitle: T\njust text\n---\nBody", "key: value")]
    [InlineData("---\nslug: s\ntitle: T\n---\n\n", "no body")]
    public void Malformed_files_fail_with_the_file_name_and_the_problem(string text, string expected)
    {
        var error = Assert.Throws<InvalidDataException>(() => SeedFiles.ParseArticle("broken.md", text));

        Assert.StartsWith("broken.md:", error.Message, StringComparison.Ordinal);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }
}
