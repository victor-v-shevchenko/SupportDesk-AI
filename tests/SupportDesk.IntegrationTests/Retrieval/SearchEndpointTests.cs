using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using SupportDesk.Api.Endpoints;

namespace SupportDesk.IntegrationTests.Retrieval;

[Collection(PostgresCollectionDefinition.Name)]
public class SearchEndpointTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Invalid_q_and_k_get_a_400_naming_the_field()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);
        var longQuery = new string('a', 501);
        (string Url, string Field)[] cases =
        [
            ("/api/search", "q"),
            ("/api/search?q=", "q"),
            ("/api/search?q=%20%20", "q"),
            ($"/api/search?q={longQuery}", "q"),
            ("/api/search?q=refund&k=0", "k"),
            ("/api/search?q=refund&k=11", "k"),
            ("/api/search?q=refund&k=-1", "k"),
            ("/api/search?q=refund&k=abc", "k"),
            ("/api/search?q=refund&k=2.5", "k"),
        ];

        foreach (var (url, field) in cases)
        {
            var response = await api.Client.GetAsync(new Uri(url, UriKind.Relative), Ct);

            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{url}: {response.StatusCode}");
            var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct);
            Assert.Equal([field], problem!.Errors.Keys);
        }
    }

    [Fact]
    public async Task A_valid_search_returns_at_most_k_results_and_k_defaults_to_5()
    {
        // The fake generator's vectors aren't similar to anything, so the minimum similarity is turned off here
        await using var api = await SeededApi.StartAsync(postgres, Ct, new Dictionary<string, string?> { ["Retrieval:MinSimilarity"] = "0" });

        var longest = await api.Client.GetFromJsonAsync<List<SearchResultResponse>>($"/api/search?q={new string('a', 500)}&k=10", Ct);
        var defaults = await api.Client.GetFromJsonAsync<List<SearchResultResponse>>("/api/search?q=refund", Ct);
        var one = await api.Client.GetFromJsonAsync<List<SearchResultResponse>>("/api/search?q=refund&k=1", Ct);

        Assert.Equal(10, longest?.Count);
        Assert.Equal(5, defaults?.Count);
        Assert.Equal(defaults![0], Assert.Single(one!));
    }

    [Fact]
    public async Task Results_below_the_minimum_similarity_are_left_out()
    {
        // With fake vectors every similarity is close to 0, under the default minimum
        await using var api = await SeededApi.StartAsync(postgres, Ct);

        var results = await api.Client.GetFromJsonAsync<List<SearchResultResponse>>("/api/search?q=refund", Ct);

        Assert.NotNull(results);
        Assert.Empty(results);
    }
}
