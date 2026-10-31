using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SupportDesk.Api.Endpoints;

namespace SupportDesk.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public class ArticleEndpointTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task List_returns_every_seeded_article_by_title_without_bodies()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);

        using var json = JsonDocument.Parse(await api.Client.GetStringAsync(new Uri("/api/articles", UriKind.Relative), Ct));

        var items = json.RootElement.EnumerateArray().ToList();
        Assert.Equal(12, items.Count);
        Assert.Equal(["slug", "title", "updatedAt"], items[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var titles = items.Select(i => i.GetProperty("title").GetString()).ToList();
        Assert.Equal(titles.Order(StringComparer.Ordinal), titles);
    }

    [Fact]
    public async Task Detail_returns_the_article_markdown_by_slug()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);

        var article = await api.Client.GetFromJsonAsync<ArticleDetailResponse>("/api/articles/shipping-times", Ct);

        Assert.NotNull(article);
        Assert.Equal("Shipping times and delivery options", article.Title);
        Assert.Contains("## Delivery options in the United States", article.BodyMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("slug:", article.BodyMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detail_returns_404_problem_details_for_an_unknown_slug()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);

        var response = await api.Client.GetAsync(new Uri("/api/articles/no-such-article", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        Assert.Equal("Article not found", problem?.Title);
    }
}
