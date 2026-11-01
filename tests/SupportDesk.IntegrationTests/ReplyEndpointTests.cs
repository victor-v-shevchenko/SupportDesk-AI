using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Api.Endpoints;
using SupportDesk.Core.AI.Usage;

namespace SupportDesk.IntegrationTests;

/// <summary><c>POST /api/tickets/{id}/reply</c> end to end, with the fake chat and embedding providers.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public class ReplyEndpointTests(PostgresFixture postgres)
{
    // The fake embedding vectors aren't similar to anything, so the minimum similarity is turned off to get sources
    private static readonly Dictionary<string, string?> AllChunks = new() { ["Retrieval:MinSimilarity"] = "0" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_unknown_ticket_is_a_404_before_any_stream()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);

        var response = await api.Client.PostAsync(new Uri("/api/tickets/999999/reply", UriKind.Relative), null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        Assert.Equal("Ticket not found", problem!.Title);
    }

    [Fact]
    public async Task A_reply_streams_tokens_then_citations_usage_and_done()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct, AllChunks);
        var ticketId = await TicketIdAsync(api, "T-1001");

        using var response = await PostReplyAsync(api, ticketId);
        var events = await ReadEventsAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var names = events.Select(e => e.Event).ToList();
        Assert.True(names.Count(n => n == "token") > 1, string.Join(",", names));
        Assert.Equal(["citations", "usage", "done"], names.SkipWhile(n => n == "token"));

        var text = string.Concat(events.Where(e => e.Event == "token").Select(e => e.Data.GetProperty("text").GetString()));
        Assert.Contains("[1]", text, StringComparison.Ordinal);

        var citations = events.Single(e => e.Event == "citations").Data;
        var citation = Assert.Single(citations.GetProperty("citations").EnumerateArray());
        Assert.Equal(1, citation.GetProperty("marker").GetInt32());
        Assert.False(string.IsNullOrEmpty(citation.GetProperty("articleSlug").GetString()));
        Assert.False(string.IsNullOrEmpty(citation.GetProperty("articleTitle").GetString()));
        Assert.Empty(citations.GetProperty("removed").EnumerateArray());

        // The default scenario's usage, at the fake model's price of 0
        var usage = events.Single(e => e.Event == "usage").Data;
        Assert.Equal(1200, usage.GetProperty("inputTokens").GetInt64());
        Assert.Equal(48, usage.GetProperty("outputTokens").GetInt64());
        Assert.Equal(0m, usage.GetProperty("costUsd").GetDecimal());
        Assert.False(usage.GetProperty("estimated").GetBoolean());

        var replyRunId = events.Single(e => e.Event == "done").Data.GetProperty("replyRunId").GetGuid();
        var row = Assert.Single(await PollAsync(api, ticketId, r => r.Count >= 1));
        Assert.Equal(replyRunId, row.ReplyRunId);
        Assert.Equal(ModelCallOutcome.Success, row.Outcome);
    }

    [Fact]
    public async Task Without_sources_the_marker_is_removed()
    {
        // The default minimum similarity keeps none of the fake vectors
        await using var api = await SeededApi.StartAsync(postgres, Ct);
        var ticketId = await TicketIdAsync(api, "T-1001");

        using var response = await PostReplyAsync(api, ticketId);
        var events = await ReadEventsAsync(response);

        var citations = events.Single(e => e.Event == "citations").Data;
        Assert.Empty(citations.GetProperty("citations").EnumerateArray());
        Assert.Equal([1], citations.GetProperty("removed").EnumerateArray().Select(n => n.GetInt32()));
    }

    [Theory]
    [InlineData("T-1021", "model_unavailable", 0)]
    [InlineData("T-1022", "model_unavailable", 1)]
    [InlineData("T-1025", "model_refused", 0)]
    public async Task A_failing_reply_ends_with_one_error_event(string ticketNumber, string code, int minTokens)
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct, AllChunks);
        var ticketId = await TicketIdAsync(api, ticketNumber);

        using var response = await PostReplyAsync(api, ticketId);
        var events = await ReadEventsAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(events.Take(events.Count - 1).All(e => e.Event == "token"), string.Join(",", events.Select(e => e.Event)));
        Assert.True(events.Count - 1 >= minTokens);
        var error = events[^1];
        Assert.Equal("error", error.Event);
        Assert.Equal(code, error.Data.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.Data.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task The_order_scenario_looks_up_the_order_and_the_reply_uses_its_status()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct, AllChunks);
        var ticketId = await TicketIdAsync(api, "T-1002");

        using var response = await PostReplyAsync(api, ticketId);
        var events = await ReadEventsAsync(response);

        var names = events.Select(e => e.Event).ToList();
        Assert.Equal("tool_call", names[0]);
        Assert.Equal(["citations", "usage", "done"], names.Skip(1).SkipWhile(n => n == "token"));
        var call = events[0].Data;
        Assert.Equal("lookup_order_status", call.GetProperty("name").GetString());
        Assert.Equal("KO-205002", call.GetProperty("arguments").GetProperty("orderNumber").GetString());
        var result = call.GetProperty("result");
        Assert.True(result.GetProperty("found").GetBoolean());
        Assert.Equal("Delayed", result.GetProperty("status").GetString());
        Assert.Equal("FedEx", result.GetProperty("carrier").GetString());
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 7, 30, 0, TimeSpan.Zero), result.GetProperty("lastUpdate").GetDateTimeOffset());
        Assert.Contains("delayed", ReplyText(events), StringComparison.Ordinal);

        // Two calls in the tool loop, one usage row each, and the usage event adds them up
        Assert.Equal(2700, events.Single(e => e.Event == "usage").Data.GetProperty("inputTokens").GetInt64());
        Assert.Equal(2, (await PollAsync(api, ticketId, r => r.Count >= 2)).Count);
    }

    [Fact]
    public async Task The_proposal_scenario_reports_the_unknown_order_and_proposes_without_creating_a_task()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct, AllChunks);
        var ticketId = await TicketIdAsync(api, "T-1008");

        using var response = await PostReplyAsync(api, ticketId);
        var events = await ReadEventsAsync(response);

        // The lookup, then the text before the proposal, the proposal, the rest of the text, then the end
        var names = events.Select(e => e.Event).ToList();
        var proposalAt = names.LastIndexOf("tool_call");
        Assert.Equal("tool_call", names[0]);
        Assert.True(proposalAt > 1 && names[1..proposalAt].All(n => n == "token"), string.Join(",", names));
        Assert.Equal(["citations", "usage", "done"], names.Skip(proposalAt + 1).SkipWhile(n => n == "token"));
        Assert.Equal("""{"found":false}""", events[0].Data.GetProperty("result").GetRawText());
        Assert.Contains("couldn't find order KO-208815", ReplyText(events), StringComparison.Ordinal);

        var proposal = events[proposalAt].Data;
        Assert.Equal("propose_follow_up_task", proposal.GetProperty("name").GetString());
        var payload = proposal.GetProperty("proposal");
        Assert.Equal("Open a carrier trace for KO-208815", payload.GetProperty("title").GetString());
        Assert.Equal("Pending", payload.GetProperty("status").GetString());
        var replyRunId = events[^1].Data.GetProperty("replyRunId").GetGuid();

        await using var db = api.CreateDbContext();
        var stored = await db.FollowUpProposals.SingleAsync(p => p.TicketId == ticketId, Ct);
        Assert.Equal(payload.GetProperty("id").GetGuid(), stored.Id);
        Assert.Equal(replyRunId, stored.ReplyRunId);
        Assert.Equal(DateOnly.FromDateTime(stored.CreatedAt.UtcDateTime).AddDays(2), stored.DueDate);
        Assert.Equal(stored.DueDate!.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), payload.GetProperty("dueDate").GetString());
        // Proposing never creates a task; the Confirm endpoint does
        Assert.False(await db.FollowUpTasks.AnyAsync(Ct));

        using var confirmed = await FollowUpTests.PostConfirmAsync(api, stored.Id);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(1, await db.FollowUpTasks.CountAsync(t => t.ProposalId == stored.Id, Ct));
    }

    [Fact]
    public async Task A_prompt_over_the_budget_ends_with_budget_exceeded()
    {
        var settings = new Dictionary<string, string?>(AllChunks) { ["AI:Budget:MaxInputTokensPerCall"] = "50" };
        await using var api = await SeededApi.StartAsync(postgres, Ct, settings);
        var ticketId = await TicketIdAsync(api, "T-1001");

        using var response = await PostReplyAsync(api, ticketId);
        var events = await ReadEventsAsync(response);

        var error = Assert.Single(events);
        Assert.Equal("error", error.Event);
        Assert.Equal("budget_exceeded", error.Data.GetProperty("code").GetString());
        Assert.Equal("This ticket is too long for a suggested reply.", error.Data.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Leaving_the_stream_cancels_the_model_call()
    {
        var logs = new LogCollector();
        await using var api = await SeededApi.StartAsync(postgres, Ct, AllChunks, logger: logs);
        var ticketId = await TicketIdAsync(api, "T-1001");

        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            using var response = await PostReplyAsync(api, ticketId, cts.Token);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
            // Wait for the first token, then leave as a browser tab does
            while (await reader.ReadLineAsync(cts.Token) is { } line && line != "event: token")
            {
            }

            await cts.CancelAsync();
        }

        var row = Assert.Single(await PollAsync(api, ticketId, r => r.Count >= 1));
        Assert.Equal(ModelCallOutcome.Cancelled, row.Outcome);
        await WaitForAsync(() => logs.All.Contains("was cancelled", StringComparison.Ordinal));
    }

    private static async Task<int> TicketIdAsync(SeededApi api, string number)
    {
        await using var db = api.CreateDbContext();
        return await db.Tickets.Where(t => t.Number == number).Select(t => t.Id).SingleAsync(Ct);
    }

    private static string ReplyText(List<(string Event, JsonElement Data)> events) =>
        string.Concat(events.Where(e => e.Event == "token").Select(e => e.Data.GetProperty("text").GetString()));

    private static Task<HttpResponseMessage> PostReplyAsync(SeededApi api, int ticketId, CancellationToken? cancellationToken = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/tickets/{ticketId}/reply", UriKind.Relative));
        return api.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken ?? Ct);
    }

    /// <summary>Reads the whole stream and splits it into frames: an <c>event:</c> line, a <c>data:</c> line, a blank line.</summary>
    private static async Task<List<(string Event, JsonElement Data)>> ReadEventsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        var events = new List<(string, JsonElement)>();
        foreach (var frame in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = frame.Split('\n');
            var name = lines.Single(l => l.StartsWith("event: ", StringComparison.Ordinal))["event: ".Length..];
            var data = lines.Single(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
            events.Add((name, JsonDocument.Parse(data).RootElement.Clone()));
        }

        return events;
    }

    private static async Task<List<ModelCallResponse>> PollAsync(SeededApi api, int ticketId, Func<List<ModelCallResponse>, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var rows = await api.Client.GetFromJsonAsync<List<ModelCallResponse>>(new Uri($"/api/model-calls?ticketId={ticketId}", UriKind.Relative), JsonDefaults.Options, Ct);
            if (done(rows!) || DateTime.UtcNow > deadline)
            {
                return rows!;
            }

            await Task.Delay(100, Ct);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(100, Ct);
        }
    }
}
