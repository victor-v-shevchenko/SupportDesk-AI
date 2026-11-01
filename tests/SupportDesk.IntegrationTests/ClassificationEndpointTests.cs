using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Api.Endpoints;
using SupportDesk.Core.Tickets;

namespace SupportDesk.IntegrationTests;

/// <summary><c>POST /api/tickets/{id}/classification</c> end to end, with the fake provider's scenarios (PLAN.md 6.8, 9).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public class ClassificationEndpointTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Classify_saves_the_result_and_the_ticket_detail_and_list_return_it()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);
        var id = await TicketIdAsync(api, "T-1001");

        var response = await api.Client.PostAsync(Classify(id), null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)))
        {
            Assert.Equal(
                ["category", "classifiedAt", "priority", "promptVersion", "reason", "status"],
                json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.Equal("Done", json.RootElement.GetProperty("status").GetString());
            Assert.Equal("Other", json.RootElement.GetProperty("category").GetString());
        }

        var result = await response.Content.ReadFromJsonAsync<TicketClassificationResponse>(JsonDefaults.Options, Ct);
        Assert.NotNull(result);
        Assert.Equal(Priority.Normal, result.Priority);
        Assert.Equal("General question with no urgency.", result.Reason);
        Assert.Equal("classify.v2", result.PromptVersion);
        Assert.NotNull(result.ClassifiedAt);

        var detail = await api.Client.GetFromJsonAsync<TicketDetailResponse>(new Uri($"/api/tickets/{id}", UriKind.Relative), JsonDefaults.Options, Ct);
        Assert.Equal(result, detail?.Classification);
        var list = await api.Client.GetFromJsonAsync<List<TicketListItemResponse>>(new Uri("/api/tickets", UriKind.Relative), JsonDefaults.Options, Ct);
        var item = list!.Single(t => t.Id == id);
        Assert.Equal((Category.Other, Priority.Normal, ClassificationStatus.Done), (item.Category!.Value, item.Priority!.Value, item.ClassificationStatus));
    }

    [Fact]
    public async Task Reclassify_replaces_the_old_result()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);
        var id = await TicketIdAsync(api, "T-1003");
        var earlier = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using (var db = api.CreateDbContext())
        {
            var ticket = await db.Tickets.SingleAsync(t => t.Id == id, Ct);
            ticket.Classification.Status = ClassificationStatus.NeedsManual;
            ticket.Classification.Reason = "old";
            ticket.Classification.PromptVersion = "classify.v1";
            ticket.Classification.ClassifiedAt = earlier;
            await db.SaveChangesAsync(Ct);
        }

        var response = await api.Client.PostAsync(Classify(id), null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using (var db = api.CreateDbContext())
        {
            var saved = (await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == id, Ct)).Classification;
            Assert.Equal(ClassificationStatus.Done, saved.Status);
            Assert.Equal(Category.Other, saved.Category);
            Assert.Equal("General question with no urgency.", saved.Reason);
            Assert.Equal("classify.v2", saved.PromptVersion);
            Assert.True(saved.ClassifiedAt > earlier);
        }
    }

    [Fact]
    public async Task Invalid_output_twice_saves_NeedsManual_with_its_own_list_status()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);
        var id = await TicketIdAsync(api, "T-1024");

        var result = await (await api.Client.PostAsync(Classify(id), null, Ct)).Content.ReadFromJsonAsync<TicketClassificationResponse>(JsonDefaults.Options, Ct);

        Assert.Equal(ClassificationStatus.NeedsManual, result?.Status);
        Assert.Null(result?.Category);
        Assert.Null(result?.Priority);
        Assert.Equal("classify.v2", result?.PromptVersion);
        var list = await api.Client.GetFromJsonAsync<List<TicketListItemResponse>>(new Uri("/api/tickets", UriKind.Relative), JsonDefaults.Options, Ct);
        Assert.Equal(ClassificationStatus.NeedsManual, list!.Single(t => t.Id == id).ClassificationStatus);
    }

    [Theory]
    [InlineData("T-1021", HttpStatusCode.ServiceUnavailable, "The AI model is unavailable")]
    [InlineData("T-1025", HttpStatusCode.UnprocessableEntity, "The AI model declined")]
    public async Task Model_errors_return_problem_details_and_leave_the_classification_unchanged(string number, HttpStatusCode status, string title)
    {
        // One attempt with no wait, so the 503 scenario doesn't sit through the backoff
        await using var api = await SeededApi.StartAsync(postgres, Ct, new Dictionary<string, string?> { ["AI:Retry:MaxAttempts"] = "1" });
        var id = await TicketIdAsync(api, number);

        var response = await api.Client.PostAsync(Classify(id), null, Ct);

        await AssertProblemAsync(response, status, title);
        var detail = await api.Client.GetFromJsonAsync<TicketDetailResponse>(new Uri($"/api/tickets/{id}", UriKind.Relative), JsonDefaults.Options, Ct);
        Assert.Equal(ClassificationStatus.NotRun, detail?.Classification.Status);
    }

    [Fact]
    public async Task A_ticket_over_the_input_budget_returns_413()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct, new Dictionary<string, string?> { ["AI:Budget:MaxInputTokensPerCall"] = "50" });
        var id = await TicketIdAsync(api, "T-1001");

        var response = await api.Client.PostAsync(Classify(id), null, Ct);

        await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, "Over the token budget");
    }

    [Fact]
    public async Task An_unknown_ticket_returns_404_problem_details()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);

        var response = await api.Client.PostAsync(Classify(999999), null, Ct);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "Ticket not found");
    }

    private static Uri Classify(int id) => new($"/api/tickets/{id}/classification", UriKind.Relative);

    private static async Task<int> TicketIdAsync(SeededApi api, string number)
    {
        await using var db = api.CreateDbContext();
        return await db.Tickets.Where(t => t.Number == number).Select(t => t.Id).SingleAsync(Ct);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string title)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var problem = JsonSerializer.Deserialize<ProblemDetails>(text, JsonDefaults.Options);
        Assert.Equal((int)status, problem?.Status);
        Assert.Equal(title, problem?.Title);
        // Fixed messages only: nothing from the provider's exception
        Assert.DoesNotContain("Exception", text, StringComparison.Ordinal);
        Assert.DoesNotContain("fake", text, StringComparison.OrdinalIgnoreCase);
    }
}
