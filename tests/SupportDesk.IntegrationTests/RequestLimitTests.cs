using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Usage;

namespace SupportDesk.IntegrationTests;

/// <summary>The rate limit and the daily spending limit on the AI endpoints (PLAN.md 5, 6.4), through the whole app.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public class RequestLimitTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ai_requests_past_the_window_limit_are_refused_and_other_endpoints_are_not()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct, new Dictionary<string, string?>
        {
            ["RateLimiting:Ai:PermitLimit"] = "2",
            ["RateLimiting:Ai:WindowSeconds"] = "3600",
        });
        var id = await IdAsync(api, "T-1001");

        // One window for every AI endpoint: a search and a summary use up the two permits
        Assert.Equal(HttpStatusCode.OK, (await api.Client.GetAsync("/api/search?q=shipping", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Client.PostAsync($"/api/tickets/{id}/summary", null, Ct)).StatusCode);
        var calls = await CountCallsAsync(api);

        var refused = await api.Client.PostAsync($"/api/tickets/{id}/reply", null, Ct);

        await AssertProblemAsync(refused, "rate_limited");
        Assert.True(refused.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        await AssertProblemAsync(await api.Client.PostAsync($"/api/tickets/{id}/classification", null, Ct), "rate_limited");
        await AssertProblemAsync(await api.Client.GetAsync("/api/search?q=returns", Ct), "rate_limited");
        Assert.Equal(calls, await CountCallsAsync(api));
        Assert.Equal(HttpStatusCode.OK, (await api.Client.GetAsync("/api/tickets", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Client.GetAsync($"/api/tickets/{id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Once_today_s_cost_reaches_the_limit_the_model_endpoints_are_refused_until_midnight_UTC()
    {
        await WaitPastMidnightIfCloseAsync();
        await using var api = await SeededApi.StartAsync(postgres, Ct, new Dictionary<string, string?> { ["AI:Budget:DailySpendLimitUsd"] = "2" });
        var id = await IdAsync(api, "T-1001");
        await AddCallsAsync(api, (DateTimeOffset.UtcNow, 1.5m), (DateTimeOffset.UtcNow, 0.5m));
        var calls = await CountCallsAsync(api);

        foreach (var path in new[] { "reply", "summary", "classification" })
        {
            var response = await api.Client.PostAsync($"/api/tickets/{id}/{path}", null, Ct);

            await AssertProblemAsync(response, "daily_limit");
            var untilMidnight = DateTimeOffset.UtcNow.UtcDateTime.Date.AddDays(1) - DateTimeOffset.UtcNow.UtcDateTime;
            Assert.InRange(response.Headers.RetryAfter!.Delta!.Value, untilMidnight - TimeSpan.FromMinutes(1), untilMidnight + TimeSpan.FromMinutes(1));
        }

        // Nothing reached the model, so no rows were added
        Assert.Equal(calls, await CountCallsAsync(api));
        // Search writes no rows, so the spending limit doesn't apply to it
        Assert.Equal(HttpStatusCode.OK, (await api.Client.GetAsync("/api/search?q=shipping", Ct)).StatusCode);
    }

    [Fact]
    public async Task Calls_before_midnight_UTC_and_unpriced_calls_do_not_count_toward_today()
    {
        await WaitPastMidnightIfCloseAsync();
        await using var api = await SeededApi.StartAsync(postgres, Ct, new Dictionary<string, string?> { ["AI:Budget:DailySpendLimitUsd"] = "2" });
        var id = await IdAsync(api, "T-1001");
        var midnight = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
        await AddCallsAsync(api, (midnight.AddSeconds(-1), 50m), (DateTimeOffset.UtcNow, 1.99m), (DateTimeOffset.UtcNow, null));

        var response = await api.Client.PostAsync($"/api/tickets/{id}/summary", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The API reads the real clock, so a test that crosses midnight UTC would move its rows into yesterday. Each test takes a
    /// few seconds; one that would start in the last minute of the day waits for the next instead.
    /// </summary>
    private static async Task WaitPastMidnightIfCloseAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var untilMidnight = now.UtcDateTime.Date.AddDays(1) - now.UtcDateTime;
        if (untilMidnight < TimeSpan.FromMinutes(1))
        {
            await Task.Delay(untilMidnight + TimeSpan.FromSeconds(1), Ct);
        }
    }

    private static async Task<int> IdAsync(SeededApi api, string number)
    {
        await using var db = api.CreateDbContext();
        return await db.Tickets.Where(t => t.Number == number).Select(t => t.Id).SingleAsync(Ct);
    }

    private static async Task AddCallsAsync(SeededApi api, params (DateTimeOffset At, decimal? Cost)[] calls)
    {
        await using var db = api.CreateDbContext();
        db.ModelCalls.AddRange(calls.Select(c => new ModelCall
        {
            Timestamp = c.At,
            Feature = AiFeature.Reply,
            Provider = "Anthropic",
            Model = "claude-opus-5-5",
            PromptVersion = "reply.v4",
            InputTokens = 1000,
            OutputTokens = 100,
            CostUsd = c.Cost,
            Outcome = ModelCallOutcome.Success,
        }));
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<int> CountCallsAsync(SeededApi api)
    {
        // The writer saves rows a moment after each call; give it time, so a row still on its way isn't missed
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        await using var db = api.CreateDbContext();
        return await db.ModelCalls.CountAsync(Ct);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("detail").GetString()));
    }
}
