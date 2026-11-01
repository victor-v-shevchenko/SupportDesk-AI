using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Api.Endpoints;
using SupportDesk.Core.Summaries;

namespace SupportDesk.IntegrationTests;

/// <summary><c>POST /api/tickets/{id}/summary</c> through the whole pipeline with the fake provider.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public class SummaryEndpointTests(PostgresFixture postgres)
{
    private const string FirstSummary = "The customer asks how long standard shipping takes. They want a delivery estimate.";
    private const string SecondSummary = "The customer wants to know when their parcel will arrive. Nothing has been tried yet.";

    /// <summary>Two sentences that fit in 600 characters, then a third that doesn't.</summary>
    private static readonly string LongSummary =
        "The customer reports that their tent order is stuck as delayed. " + new string('a', 300) + " is the tracking code they quoted. " + new string('b', 300) + " ends the third sentence.";

    /// <summary>
    /// The tests' own scenarios: one summary per call to T-1001 (the history is the same each time, so the turn is picked by
    /// the ticket), a long one on T-1002, whitespace on T-1003, and the committed error scenarios' numbers for the rest.
    /// </summary>
    private static readonly Lazy<string> ScenariosPath = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "summary-scenarios.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            scenarios = new object[]
            {
                Scenario("default", null, Text(FirstSummary)),
                Scenario("second", "T-1004", Text(SecondSummary)),
                Scenario("long", "T-1002", Text(LongSummary)),
                Scenario("whitespace", "T-1003", Text("   \n  ")),
                Scenario("unavailable", "T-1021", new { type = "error", statusCode = 503 }),
                Scenario("stall", "T-1023", new { type = "stall", seconds = 30 }, new { type = "text", text = "Too late." }),
                Scenario("refusal", "T-1025", new { type = "finish", reason = "content_filter" }),
            },
        }));
        return path;
    });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Generating_saves_the_summary_with_its_prompt_version_and_time_and_the_ticket_returns_it()
    {
        await using var api = await StartAsync();
        var id = await IdAsync(api, "T-1001");
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var response = await api.Client.PostAsync(SummaryUri(id), null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<TicketSummaryResponse>(JsonDefaults.Options, Ct);
        Assert.Equal(FirstSummary, summary!.Text);
        Assert.Equal("summary.v2", summary.PromptVersion);
        Assert.InRange(summary.GeneratedAt, before, DateTimeOffset.UtcNow.AddSeconds(1));
        var ticket = await api.Client.GetFromJsonAsync<TicketDetailResponse>($"/api/tickets/{id}", JsonDefaults.Options, Ct);
        Assert.Equal(summary, ticket!.Summary);
    }

    [Fact]
    public async Task Regenerating_replaces_the_saved_summary()
    {
        await using var api = await StartAsync();
        var id = await IdAsync(api, "T-1004");
        await SetSummaryAsync(api, id, "An old summary.", "summary.v1", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

        var response = await api.Client.PostAsync(SummaryUri(id), null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ticket = await api.Client.GetFromJsonAsync<TicketDetailResponse>($"/api/tickets/{id}", JsonDefaults.Options, Ct);
        Assert.Equal(SecondSummary, ticket!.Summary!.Text);
        Assert.Equal("summary.v2", ticket.Summary.PromptVersion);
        Assert.True(ticket.Summary.GeneratedAt > new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_long_summary_is_cut_at_the_last_sentence_that_fits()
    {
        await using var api = await StartAsync();
        var id = await IdAsync(api, "T-1002");

        var summary = await (await api.Client.PostAsync(SummaryUri(id), null, Ct)).Content.ReadFromJsonAsync<TicketSummaryResponse>(JsonDefaults.Options, Ct);

        Assert.True(summary!.Text.Length <= SummaryOutput.MaxLength);
        Assert.EndsWith("is the tracking code they quoted.", summary.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_output_returns_502_and_leaves_the_old_summary_unchanged()
    {
        await using var api = await StartAsync();
        var id = await IdAsync(api, "T-1003");
        var at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        await SetSummaryAsync(api, id, "A good summary.", "summary.v1", at);

        var response = await api.Client.PostAsync(SummaryUri(id), null, Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadGateway, "invalid_output");
        var ticket = await api.Client.GetFromJsonAsync<TicketDetailResponse>($"/api/tickets/{id}", JsonDefaults.Options, Ct);
        Assert.Equal(new TicketSummaryResponse("A good summary.", "summary.v1", at), ticket!.Summary);
    }

    [Theory]
    [InlineData("T-1021", HttpStatusCode.ServiceUnavailable, "model_unavailable")]
    [InlineData("T-1025", HttpStatusCode.UnprocessableEntity, "model_refused")]
    public async Task Model_errors_return_problem_details_and_leave_the_old_summary_unchanged(string number, HttpStatusCode status, string code)
    {
        await using var api = await StartAsync();
        var id = await IdAsync(api, number);
        var at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        await SetSummaryAsync(api, id, "A good summary.", "summary.v1", at);

        var response = await api.Client.PostAsync(SummaryUri(id), null, Ct);

        await AssertProblemAsync(response, status, code);
        var ticket = await api.Client.GetFromJsonAsync<TicketDetailResponse>($"/api/tickets/{id}", JsonDefaults.Options, Ct);
        Assert.Equal(new TicketSummaryResponse("A good summary.", "summary.v1", at), ticket!.Summary);
    }

    [Fact]
    public async Task A_timeout_returns_503_with_the_timeout_code()
    {
        await using var api = await StartAsync(new Dictionary<string, string?>
        {
            ["AI:Timeouts:NonStreamingSeconds"] = "1",
            ["AI:Retry:MaxAttempts"] = "1",
        });
        var id = await IdAsync(api, "T-1023");

        var response = await api.Client.PostAsync(SummaryUri(id), null, Ct);

        var problem = await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "timeout");
        Assert.Contains("too long", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ticket_over_the_input_budget_returns_413_without_calling_the_model()
    {
        await using var api = await StartAsync(new Dictionary<string, string?> { ["AI:Budget:MaxInputTokensPerCall"] = "50" });
        var id = await IdAsync(api, "T-1001");

        var response = await api.Client.PostAsync(SummaryUri(id), null, Ct);

        await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, "budget_exceeded");
        var ticket = await api.Client.GetFromJsonAsync<TicketDetailResponse>($"/api/tickets/{id}", JsonDefaults.Options, Ct);
        Assert.Null(ticket!.Summary);
    }

    [Fact]
    public async Task An_unknown_ticket_returns_404_problem_details()
    {
        await using var api = await StartAsync();

        var response = await api.Client.PostAsync(SummaryUri(999_999), null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    private Task<SeededApi> StartAsync(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?> { ["AI:Fake:ScenariosPath"] = ScenariosPath.Value };
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        return SeededApi.StartAsync(postgres, Ct, settings);
    }

    private static Uri SummaryUri(int id) => new($"/api/tickets/{id}/summary", UriKind.Relative);

    private static async Task<int> IdAsync(SeededApi api, string number)
    {
        await using var db = api.CreateDbContext();
        return await db.Tickets.Where(t => t.Number == number).Select(t => t.Id).SingleAsync(Ct);
    }

    private static async Task SetSummaryAsync(SeededApi api, int id, string text, string promptVersion, DateTimeOffset at)
    {
        await using var db = api.CreateDbContext();
        var ticket = await db.Tickets.SingleAsync(t => t.Id == id, Ct);
        ticket.Summary = new() { Text = text, PromptVersion = promptVersion, GeneratedAt = at };
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<ProblemDetails> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadAsStringAsync(Ct);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        var problem = JsonSerializer.Deserialize<ProblemDetails>(json, JsonDefaults.Options)!;
        Assert.False(string.IsNullOrWhiteSpace(problem.Title));
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail));
        // Our own text, never an exception's
        Assert.DoesNotContain("Exception", json, StringComparison.Ordinal);
        Assert.DoesNotContain("tokens", json, StringComparison.Ordinal);
        return problem;
    }

    private static object Scenario(string name, string? ticketNumber, params object[] steps) => new
    {
        name,
        ticketNumber,
        turns = new Dictionary<string, object[]>(StringComparer.Ordinal)
        {
            ["*"] = [new { steps = steps.Append(new { type = "usage", inputTokens = 300, outputTokens = 20 }).ToArray() }],
        },
    };

    private static object Text(string text) => new { type = "text", text, chunkSize = 1000, delayMs = 0 };
}
