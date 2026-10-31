using Microsoft.AspNetCore.Http.HttpResults;
using SupportDesk.Core.Articles;

namespace SupportDesk.Api.Endpoints;

public static class ArticleEndpoints
{
    public static IEndpointRouteBuilder MapArticleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/articles");
        group.MapGet("/", ListArticles).WithName("ListArticles");
        group.MapGet("/{slug}", GetArticle).WithName("GetArticle");
        return app;
    }

    public static async Task<Ok<List<ArticleListItemResponse>>> ListArticles(IArticleRepository articles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(articles);

        var list = await articles.ListAsync(cancellationToken);
        return TypedResults.Ok(list.Select(a => new ArticleListItemResponse(a.Slug, a.Title, a.UpdatedAt)).ToList());
    }

    public static async Task<Results<Ok<ArticleDetailResponse>, ProblemHttpResult>> GetArticle(
        string slug, IArticleRepository articles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(articles);

        var article = await articles.FindBySlugAsync(slug, cancellationToken);
        return article is null
            ? TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Article not found", detail: "There is no article with this slug.")
            : TypedResults.Ok(new ArticleDetailResponse(article.Slug, article.Title, article.BodyMarkdown, article.UpdatedAt));
    }
}

public sealed record ArticleListItemResponse(string Slug, string Title, DateTimeOffset UpdatedAt);

public sealed record ArticleDetailResponse(string Slug, string Title, string BodyMarkdown, DateTimeOffset UpdatedAt);
