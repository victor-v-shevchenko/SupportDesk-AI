using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using SupportDesk.Api.Endpoints;
using SupportDesk.Core.AI.Fake;

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
    public async Task A_search_whose_embedding_never_returns_gets_a_503_timeout_problem()
    {
        var time = new FakeTimeProvider();
        using var stalling = new StallingEmbeddingGenerator(new FakeEmbeddingGenerator(1024));
        // The generator embeds normally while the API starts (the startup sync), then stalls
        await using var api = await SeededApi.StartAsync(postgres, Ct, services: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(time);
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(stalling);
        });
        stalling.Hang = true;

        var request = api.Client.GetAsync(new Uri("/api/search?q=refund", UriKind.Relative), Ct);
        await stalling.Stalled.WaitAsync(Ct);
        time.Advance(TimeSpan.FromSeconds(30));

        using var response = await request;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        Assert.Equal("timeout", problem!.Extensions["code"]?.ToString());
    }

    [Fact]
    public async Task A_down_embedding_provider_gets_a_503_model_unavailable_problem()
    {
        using var failing = new StallingEmbeddingGenerator(new FakeEmbeddingGenerator(1024));
        await using var api = await SeededApi.StartAsync(postgres, Ct, services: services =>
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(failing));
        failing.Failure = new HttpRequestException("Connection refused", null, null);

        var response = await api.Client.GetAsync(new Uri("/api/search?q=refund", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        Assert.Equal("model_unavailable", problem!.Extensions["code"]?.ToString());
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
