using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Api.Endpoints;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Usage;

namespace SupportDesk.IntegrationTests;

/// <summary>Usage rows from the pipeline to PostgreSQL through the background writer, and <c>GET /api/model-calls</c>.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public class ModelCallTests(PostgresFixture postgres)
{
    private static readonly Dictionary<string, string?> FakeProvider = new()
    {
        ["AI:Chat:Provider"] = "Fake",
        ["AI:Embeddings:Provider"] = "Fake",
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_model_call_writes_a_row_that_the_endpoint_returns()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct, FakeProvider);
        int ticketId;
        await using (var db = api.CreateDbContext())
        {
            ticketId = await db.Tickets.Where(t => t.Number == "T-1001").Select(t => t.Id).SingleAsync(Ct);
        }

        var chat = api.Factory.Services.GetRequiredService<IChatClient>();
        var replyRunId = Guid.NewGuid();
        var options = new AiCallContext(AiFeature.Reply, "reply.v1", ticketId, "T-1001", replyRunId).ApplyTo(new ChatOptions());
        await foreach (var _ in chat.GetStreamingResponseAsync("hi", options, Ct))
        {
            // Read it all, as a feature would
        }

        var rows = await PollAsync(api, ticketId, expected: 1);

        var row = Assert.Single(rows);
        Assert.Equal(AiFeature.Reply, row.Feature);
        Assert.Equal("reply.v1", row.PromptVersion);
        Assert.Equal(replyRunId, row.ReplyRunId);
        Assert.Equal("fake", row.Provider);
        Assert.Equal("fake", row.Model);
        // The default scenario's usage, and the fake model's zero price from appsettings.json
        Assert.Equal(1200, row.InputTokens);
        Assert.Equal(48, row.OutputTokens);
        Assert.Equal(0m, row.CostUsd);
        Assert.False(row.IsEstimated);
        Assert.Equal(ModelCallOutcome.Success, row.Outcome);
    }

    [Fact]
    public async Task The_endpoint_returns_rows_newest_first_and_filters_by_ticket()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct, FakeProvider);
        var start = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        await using (var db = api.CreateDbContext())
        {
            db.ModelCalls.AddRange(
                Row(start, ticketId: 1, AiFeature.Summary),
                Row(start.AddMinutes(2), ticketId: 1, AiFeature.Reply),
                Row(start.AddMinutes(1), ticketId: 2, AiFeature.Classification),
                Row(start.AddMinutes(3), ticketId: null, AiFeature.Reply));
            await db.SaveChangesAsync(Ct);
        }

        var forTicket = await api.Client.GetFromJsonAsync<List<ModelCallResponse>>(new Uri("/api/model-calls?ticketId=1", UriKind.Relative), JsonDefaults.Options, Ct);
        var all = await api.Client.GetFromJsonAsync<List<ModelCallResponse>>(new Uri("/api/model-calls", UriKind.Relative), JsonDefaults.Options, Ct);

        Assert.Equal([AiFeature.Reply, AiFeature.Summary], forTicket!.Select(r => r.Feature));
        Assert.Equal([start.AddMinutes(3), start.AddMinutes(2), start.AddMinutes(1), start], all!.Select(r => r.Timestamp));
    }

    [Fact]
    public async Task The_endpoint_uses_names_for_enums_in_json()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct, FakeProvider);
        await using (var db = api.CreateDbContext())
        {
            db.ModelCalls.Add(Row(DateTimeOffset.UtcNow, ticketId: 5, AiFeature.Summary));
            await db.SaveChangesAsync(Ct);
        }

        var json = await api.Client.GetStringAsync(new Uri("/api/model-calls?ticketId=5", UriKind.Relative), Ct);

        Assert.Contains("\"feature\":\"Summary\"", json, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"Success\"", json, StringComparison.Ordinal);
    }

    /// <summary>The writer is asynchronous, so the row shows up shortly after the call.</summary>
    private static async Task<List<ModelCallResponse>> PollAsync(SeededApi api, int ticketId, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var rows = await api.Client.GetFromJsonAsync<List<ModelCallResponse>>(new Uri($"/api/model-calls?ticketId={ticketId}", UriKind.Relative), JsonDefaults.Options, Ct);
            if (rows!.Count >= expected || DateTime.UtcNow > deadline)
            {
                return rows;
            }

            await Task.Delay(100, Ct);
        }
    }

    private static ModelCall Row(DateTimeOffset at, int? ticketId, AiFeature feature) => new()
    {
        Timestamp = at,
        Feature = feature,
        Provider = "fake",
        Model = "fake",
        PromptVersion = "reply.v1",
        InputTokens = 10,
        OutputTokens = 2,
        CostUsd = 0m,
        LatencyMs = 5,
        Outcome = ModelCallOutcome.Success,
        TicketId = ticketId,
    };
}
