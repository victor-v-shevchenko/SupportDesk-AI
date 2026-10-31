namespace SupportDesk.Core.Articles;

public interface IArticleRepository
{
    /// <summary>All articles, ordered by title.</summary>
    Task<IReadOnlyList<Article>> ListAsync(CancellationToken cancellationToken);

    Task<Article?> FindBySlugAsync(string slug, CancellationToken cancellationToken);
}
