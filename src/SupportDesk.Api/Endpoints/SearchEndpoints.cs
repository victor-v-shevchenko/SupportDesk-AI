using Microsoft.AspNetCore.Http.HttpResults;
using SupportDesk.Core.Retrieval;

namespace SupportDesk.Api.Endpoints;

public static class SearchEndpoints
{
    public const int QueryMaxLength = 500;
    public const int DefaultK = 5;
    public const int MaxK = 10;

    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/search", Search).WithName("Search");
        return app;
    }

    /// <summary><c>k</c> arrives as a string, so a value that isn't a number gets the same field error as one out of range.</summary>
    public static async Task<Results<Ok<List<SearchResultResponse>>, ValidationProblem>> Search(
        string? q, string? k, IRetrievalService retrieval, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retrieval);

        var (query, count, errors) = Validate(q, k);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var results = await retrieval.SearchAsync(query, count, cancellationToken);
        return TypedResults.Ok(results.Select(r => new SearchResultResponse(r.ArticleSlug, r.ArticleTitle, r.Heading, r.Text, r.Similarity)).ToList());
    }

    public static (string Query, int K, Dictionary<string, string[]> Errors) Validate(string? q, string? k)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var query = q?.Trim() ?? "";
        if (query.Length == 0)
        {
            errors["q"] = ["Enter something to search for."];
        }
        else if (query.Length > QueryMaxLength)
        {
            errors["q"] = [$"The search can be at most {QueryMaxLength} characters."];
        }

        var count = DefaultK;
        if (k is not null && (!int.TryParse(k, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out count) || count is < 1 or > MaxK))
        {
            errors["k"] = [$"k must be a whole number from 1 to {MaxK}."];
        }

        return (query, count, errors);
    }
}

/// <summary>One search hit; <see cref="Similarity"/> is cosine similarity (1 is identical).</summary>
public sealed record SearchResultResponse(string ArticleSlug, string ArticleTitle, string? Heading, string Text, double Similarity);
