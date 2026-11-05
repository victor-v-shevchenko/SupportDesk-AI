using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using SupportDesk.Api.Endpoints;
using SupportDesk.Core.Telemetry;

namespace SupportDesk.IntegrationTests;

/// <summary>
/// The counters against the table (PLAN.md 14): the API in memory with a real database, replies on T-1002 and T-1008, and the
/// sums of what the counters recorded against the <c>ModelCalls</c> rows the requests wrote.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public class MetricsTests(PostgresFixture postgres)
{
    // The fake model gets a price, so the cost counter has something to add up (index 6 in appsettings.json's AI:Pricing is "fake")
    private static readonly Dictionary<string, string?> Settings = new()
    {
        ["Retrieval:MinSimilarity"] = "0",
        ["AI:Pricing:6:Model"] = "fake",
        ["AI:Pricing:6:InputPerMTok"] = "1000",
        ["AI:Pricing:6:OutputPerMTok"] = "2000",
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Tokens_cost_and_calls_equal_the_model_call_rows_and_follow_ups_count_the_proposal_and_its_confirmation()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct, Settings);
        var factory = api.Factory.Services.GetRequiredService<IMeterFactory>();
        using var calls = Collector<long>(factory, SupportDeskMetrics.ModelCallsName);
        using var tokens = Collector<long>(factory, SupportDeskMetrics.TokensName);
        using var cost = Collector<double>(factory, SupportDeskMetrics.CostName);
        using var followUps = Collector<long>(factory, SupportDeskMetrics.FollowUpsName);
        using var replies = Collector<long>(factory, SupportDeskMetrics.RepliesName);
        var first = await TicketIdAsync(api, "T-1002");
        var second = await TicketIdAsync(api, "T-1008");

        await ReplyAsync(api, first);
        await ReplyAsync(api, second);

        var rows = await RowsAsync(api, [first, second], calls);
        Assert.Equal(rows.Count, calls.GetMeasurementSnapshot().Sum(m => m.Value));
        Assert.Equal(rows.Sum(r => r.InputTokens), Sum(tokens, "direction", "input"));
        Assert.Equal(rows.Sum(r => r.OutputTokens), Sum(tokens, "direction", "output"));
        Assert.True(rows.Sum(r => r.InputTokens) > 0);
        var expectedCost = (double)rows.Sum(r => r.CostUsd ?? 0m);
        Assert.True(expectedCost > 0, "The fake model has a price, so the replies cost something.");
        Assert.Equal(expectedCost, cost.GetMeasurementSnapshot().Sum(m => m.Value), 8);
        Assert.Equal(2, replies.GetMeasurementSnapshot().Where(m => Equals(m.Tags["outcome"], "done")).Sum(m => m.Value));

        // T-1008 proposes a task; confirming it counts once, and confirming again returns the task without counting again
        Assert.Equal(1, Sum(followUps, "event", "proposed"));
        await using var db = api.CreateDbContext();
        var proposal = await db.FollowUpProposals.SingleAsync(p => p.TicketId == second, Ct);
        using var confirmed = await FollowUpTests.PostConfirmAsync(api, proposal.Id);
        using var again = await FollowUpTests.PostConfirmAsync(api, proposal.Id);
        confirmed.EnsureSuccessStatusCode();
        again.EnsureSuccessStatusCode();
        Assert.Equal(1, Sum(followUps, "event", "confirmed"));
    }

    [Fact]
    public async Task Confirming_a_proposal_past_its_expiry_counts_it_as_expired_once_and_not_as_confirmed()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct, Settings);
        var factory = api.Factory.Services.GetRequiredService<IMeterFactory>();
        using var followUps = Collector<long>(factory, SupportDeskMetrics.FollowUpsName);
        var ticketId = await TicketIdAsync(api, "T-1008");
        await ReplyAsync(api, ticketId);
        await using var db = api.CreateDbContext();
        var proposal = await db.FollowUpProposals.SingleAsync(p => p.TicketId == ticketId, Ct);
        await db.FollowUpProposals.Where(p => p.Id == proposal.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ExpiresAt, p => p.CreatedAt - TimeSpan.FromMinutes(1)), Ct);

        using var late = await FollowUpTests.PostConfirmAsync(api, proposal.Id);
        using var later = await FollowUpTests.PostConfirmAsync(api, proposal.Id);

        Assert.Equal(System.Net.HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, later.StatusCode);
        Assert.Equal(1, Sum(followUps, "event", "expired"));
        Assert.Equal(0, Sum(followUps, "event", "confirmed"));
    }

    private static MetricCollector<T> Collector<T>(IMeterFactory factory, string instrument)
        where T : struct => new(factory, SupportDeskMetrics.MeterName, instrument);

    private static long Sum(MetricCollector<long> collector, string key, string value) =>
        collector.GetMeasurementSnapshot().Where(m => Equals(m.Tags[key], value)).Sum(m => m.Value);

    private static async Task ReplyAsync(SeededApi api, int ticketId)
    {
        using var response = await api.Client.PostAsync(new Uri($"/api/tickets/{ticketId}/reply", UriKind.Relative), null, Ct);
        response.EnsureSuccessStatusCode();
        // The stream is read to its end, so the reply has finished and recorded its outcome
        await response.Content.ReadAsStringAsync(Ct);
    }

    /// <summary>The rows are saved by a background writer, so wait until there are as many as the counter has seen calls.</summary>
    private static async Task<List<ModelCallResponse>> RowsAsync(SeededApi api, int[] ticketIds, MetricCollector<long> calls)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var rows = new List<ModelCallResponse>();
            foreach (var id in ticketIds)
            {
                rows.AddRange((await api.Client.GetFromJsonAsync<List<ModelCallResponse>>(new Uri($"/api/model-calls?ticketId={id}", UriKind.Relative), JsonDefaults.Options, Ct))!);
            }

            if (rows.Count >= calls.GetMeasurementSnapshot().Sum(m => m.Value) || DateTime.UtcNow > deadline)
            {
                return rows;
            }

            await Task.Delay(100, Ct);
        }
    }

    private static async Task<int> TicketIdAsync(SeededApi api, string number)
    {
        await using var db = api.CreateDbContext();
        return await db.Tickets.Where(t => t.Number == number).Select(t => t.Id).SingleAsync(Ct);
    }
}
