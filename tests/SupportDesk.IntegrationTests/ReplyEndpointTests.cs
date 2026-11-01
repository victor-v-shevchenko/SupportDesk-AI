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
