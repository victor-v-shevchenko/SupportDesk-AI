namespace SupportDesk.Core.Articles;

public sealed class Article
{
    public const int SlugMaxLength = 100;
    public const int TitleMaxLength = 200;

    public int Id { get; set; }

    public required string Slug { get; set; }

    public required string Title { get; set; }

    public required string BodyMarkdown { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
