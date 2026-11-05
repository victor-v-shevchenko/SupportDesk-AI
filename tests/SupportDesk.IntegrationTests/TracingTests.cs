using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using SupportDesk.Api.Endpoints;
using SupportDesk.Api.Telemetry;
using SupportDesk.Core.Telemetry;

namespace SupportDesk.IntegrationTests;

/// <summary>
/// What a request leaves in a trace (PLAN.md 14): the API in memory with the app's tracing (<c>AddSupportDeskSources</c>)
/// and an in-memory exporter, against a real database, so the SQL spans are Npgsql's own.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public class TracingTests(PostgresFixture postgres)
{
    private static readonly Dictionary<string, string?> AllChunks = new() { ["Retrieval:MinSimilarity"] = "0" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_reply_on_T_1002_is_one_trace_from_the_http_request_down_to_the_sql_and_its_chat_spans_match_the_model_call_rows()
    {
        var spans = new List<Activity>();
        await using var api = await StartAsync(spans);
        var ticketId = await TicketIdAsync(api, "T-1002");

        using var response = await PostAsync(api, $"/api/tickets/{ticketId}/reply");
        await response.Content.ReadAsStringAsync(Ct);

        var server = await WaitForAsync(spans, a => a.Kind == ActivityKind.Server && a.DisplayName.Contains("/reply", StringComparison.Ordinal));
        var rows = await ModelCallRowsAsync(api, ticketId, 2);
        var trace = await WaitForTraceAsync(spans, server, t => t.Count(a => a.OperationName.StartsWith("chat", StringComparison.Ordinal)) >= rows.Count);

        var reply = Assert.Single(trace, a => a.OperationName == "reply");
        Assert.Equal(server.SpanId, reply.ParentSpanId);
        var retrieval = Assert.Single(trace, a => a.OperationName == "retrieval");
        Assert.Equal(reply.SpanId, retrieval.ParentSpanId);
        var embeddings = Assert.Single(trace, a => a.OperationName.StartsWith("embeddings", StringComparison.Ordinal));
        Assert.Equal(retrieval.SpanId, embeddings.ParentSpanId);
        var chats = trace.Where(a => a.OperationName.StartsWith("chat", StringComparison.Ordinal)).ToList();
        Assert.Equal(rows.Count, chats.Count);
        Assert.All(chats, chat => Assert.Equal(reply.SpanId, ParentOf(trace, chat, "reply")?.SpanId));
        Assert.Single(trace, a => a.OperationName.StartsWith("execute_tool", StringComparison.Ordinal));
        var sql = trace.Where(a => a.TagObjects.Any(t => t.Key.StartsWith("db.system", StringComparison.Ordinal) && Equals(t.Value, "postgresql"))).ToList();
        Assert.NotEmpty(sql);
        Assert.All(sql, s => Assert.NotEqual(default, s.ParentSpanId));

        // Everything is in the one trace, below the request
        Assert.All(trace, a => Assert.Equal(server.TraceId, a.TraceId));

        // No text from the ticket reached any span, SQL included
        await using var db = api.CreateDbContext();
        var ticket = await db.Tickets.SingleAsync(t => t.Id == ticketId, Ct);
        var text = trace.SelectMany(AllText).ToList();
        foreach (var secret in new[] { ticket.Subject, ticket.Body, ticket.CustomerName })
        {
            Assert.DoesNotContain(text, t => t.Contains(secret, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task The_health_endpoint_leaves_no_span_but_other_requests_do()
    {
        var spans = new List<Activity>();
        await using var api = await StartAsync(spans);

        using var health = await api.Client.GetAsync(new Uri("/api/health", UriKind.Relative), Ct);
        using var tickets = await api.Client.GetAsync(new Uri("/api/tickets", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        await WaitForAsync(spans, a => a.Kind == ActivityKind.Server && a.DisplayName.Contains("/api/tickets", StringComparison.Ordinal));
        Assert.DoesNotContain(Snapshot(spans), a => a.DisplayName.Contains("/api/health", StringComparison.Ordinal)
            || a.TagObjects.Any(t => Equals(t.Value, "/api/health")));
    }

    [Fact]
    public async Task Confirming_a_proposal_and_saving_model_calls_have_their_spans()
    {
        var spans = new List<Activity>();
        await using var api = await StartAsync(spans);
        var ticketId = await TicketIdAsync(api, "T-1008");
        using var reply = await PostAsync(api, $"/api/tickets/{ticketId}/reply");
        await reply.Content.ReadAsStringAsync(Ct);
        var rows = await ModelCallRowsAsync(api, ticketId, 2);
        await using var db = api.CreateDbContext();
        var proposal = await db.FollowUpProposals.SingleAsync(p => p.TicketId == ticketId, Ct);

        using var confirmed = await PostAsync(api, $"/api/follow-ups/{proposal.Id}/confirm");

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var confirm = await WaitForAsync(spans, a => a.OperationName == "confirm_follow_up");
        Assert.Equal("Confirmed", confirm.GetTagItem("supportdesk.outcome"));
        Assert.Equal(2, confirm.GetTagItem("supportdesk.rows_changed"));
        Assert.NotEqual(default, confirm.ParentSpanId);

        // The writer runs in the background, so its batches are traces of their own
        var saves = Snapshot(spans).Where(a => a.OperationName == "model_calls.save").ToList();
        Assert.NotEmpty(saves);
        Assert.All(saves, s => Assert.Equal(default, s.ParentSpanId));
        Assert.Equal(rows.Count, saves.Sum(s => (int)s.GetTagItem("supportdesk.row_count")!));
    }

    [Fact]
    public async Task A_summary_is_a_span_with_its_chat_span_below_it_and_the_expiry_check_a_root_span()
    {
        var spans = new List<Activity>();
        await using var api = await StartAsync(spans);
        var ticketId = await TicketIdAsync(api, "T-1001");

        using var response = await PostAsync(api, $"/api/tickets/{ticketId}/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await WaitForAsync(spans, a => a.OperationName == "summary");
        Assert.Equal(ticketId, summary.GetTagItem(SupportDeskTelemetry.TicketId));
        var trace = await WaitForTraceAsync(spans, summary, t => t.Any(a => a.OperationName.StartsWith("chat", StringComparison.Ordinal)));
        Assert.Equal("Summary", trace.Single(a => a.OperationName.StartsWith("chat", StringComparison.Ordinal)).GetTagItem("supportdesk.feature"));

        // Runs at startup and then on a timer: no request is its parent
        var expiry = Snapshot(spans).First(a => a.OperationName == "proposal_expiry");
        Assert.Equal(default, expiry.ParentSpanId);
        Assert.NotNull(expiry.GetTagItem("supportdesk.rows_changed"));
    }

    [Fact]
    public async Task A_failed_sql_command_keeps_its_span_and_leaves_no_exception_message_or_stack_in_any_span()
    {
        const string Marker = "SQLMARKER-4d2e";
        var spans = new List<Activity>();
        await using var api = await StartAsync(spans);
        using var enclosing = new ActivitySource("SupportDesk.IntegrationTests");
        using var tracing = new ActivityListener { ShouldListenTo = s => s.Name == enclosing.Name, Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded };
        ActivitySource.AddActivityListener(tracing);

        Activity? parent;
        using (parent = enclosing.StartActivity("enclosing"))
        using (var scope = api.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SupportDesk.Data.SupportDeskDbContext>();
            // A good command first, to compare names with; the bad one sends the marker as a parameter, so only PostgreSQL's
            // message quotes it: invalid input syntax for type integer: "SQLMARKER-4d2e"
            await db.Database.ExecuteSqlAsync($"SELECT 1", Ct);
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlAsync($"SELECT {Marker}::int", Ct));
        }

        Assert.NotNull(parent);
        await WaitForAsync(spans, a => a.Status == ActivityStatusCode.Error && a.TagObjects.Any(t => t.Key.StartsWith("db.", StringComparison.Ordinal)));
        var all = Snapshot(spans);
        var failed = Assert.Single(all, a => a.Status == ActivityStatusCode.Error && a.TagObjects.Any(t => t.Key.StartsWith("db.", StringComparison.Ordinal)));
        var good = all.First(a => a.Status != ActivityStatusCode.Error && a.Source.Name == "Npgsql" && a.ParentSpanId == parent.SpanId);

        // The span is Npgsql's own, with its place in the trace
        Assert.Equal("Npgsql", failed.Source.Name);
        Assert.Equal(parent.TraceId, failed.TraceId);
        Assert.Equal(parent.SpanId, failed.ParentSpanId);
        Assert.Equal(good.DisplayName, failed.DisplayName);
        Assert.Equal(good.Kind, failed.Kind);

        // The message and stack are placeholders, and the status holds no text
        Assert.DoesNotContain(all.SelectMany(AllText), text => text.Contains(Marker, StringComparison.Ordinal));
        var tags = Assert.Single(failed.Events, e => e.Name == "exception").Tags.ToDictionary(t => t.Key, t => t.Value as string);
        Assert.Equal(tags["exception.type"], tags["exception.message"]);
        Assert.Equal("", tags["exception.stacktrace"]);
        // Npgsql's own description is the SQLSTATE, a code
        Assert.Equal("22P02", failed.StatusDescription);
    }

    [Fact]
    public async Task A_search_query_stays_out_of_every_span()
    {
        const string Marker = "QUERYMARKER-77aa";
        var spans = new List<Activity>();
        await using var api = await StartAsync(spans);

        using var response = await api.Client.GetAsync(new Uri($"/api/search?q={Marker}", UriKind.Relative), Ct);
        await WaitForAsync(spans, a => a.Kind == ActivityKind.Server && a.DisplayName.Contains("/api/search", StringComparison.Ordinal));
        await Task.Delay(300, Ct);

        Assert.DoesNotContain(Snapshot(spans).SelectMany(AllText), text => text.Contains(Marker, StringComparison.Ordinal));
    }

    /// <summary>The API in Development with the fake providers, tracing on, spans into <paramref name="spans"/>.</summary>
    private Task<SeededApi> StartAsync(List<Activity> spans) =>
        SeededApi.StartAsync(
            postgres,
            Ct,
            AllChunks,
            services => services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSupportDeskSources().AddInMemoryExporter(spans)));

    private static Task<HttpResponseMessage> PostAsync(SeededApi api, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        return api.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
    }

    private static async Task<int> TicketIdAsync(SeededApi api, string number)
    {
        await using var db = api.CreateDbContext();
        return await db.Tickets.Where(t => t.Number == number).Select(t => t.Id).SingleAsync(Ct);
    }

    private static async Task<List<ModelCallResponse>> ModelCallRowsAsync(SeededApi api, int ticketId, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var rows = await api.Client.GetFromJsonAsync<List<ModelCallResponse>>(new Uri($"/api/model-calls?ticketId={ticketId}", UriKind.Relative), JsonDefaults.Options, Ct);
            if (rows!.Count >= count || DateTime.UtcNow > deadline)
            {
                return rows;
            }

            await Task.Delay(100, Ct);
        }
    }

    private static List<Activity> Snapshot(List<Activity> spans)
    {
        lock (spans)
        {
            return [.. spans];
        }
    }

    private static async Task<Activity> WaitForAsync(List<Activity> spans, Func<Activity, bool> match)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            if (Snapshot(spans).FirstOrDefault(match) is { } found)
            {
                return found;
            }

            Assert.True(DateTime.UtcNow < deadline, "The span never ended.");
            await Task.Delay(50, Ct);
        }
    }

    /// <summary>The spans of <paramref name="root"/>'s trace, once <paramref name="ready"/> holds (spans end a little after the response).</summary>
    private static async Task<List<Activity>> WaitForTraceAsync(List<Activity> spans, Activity root, Func<List<Activity>, bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var trace = Snapshot(spans).Where(a => a.TraceId == root.TraceId).ToList();
            if (ready(trace) || DateTime.UtcNow > deadline)
            {
                return trace;
            }

            await Task.Delay(50, Ct);
        }
    }

    private static Activity? ParentOf(List<Activity> trace, Activity span, string name)
    {
        for (var parent = trace.FirstOrDefault(a => a.SpanId == span.ParentSpanId); parent is not null; parent = trace.FirstOrDefault(a => a.SpanId == parent.ParentSpanId))
        {
            if (parent.OperationName == name)
            {
                return parent;
            }
        }

        return null;
    }

    private static IEnumerable<string> AllText(Activity span)
    {
        yield return span.DisplayName;
        yield return span.StatusDescription ?? "";
        foreach (var tag in span.TagObjects)
        {
            yield return tag.Value is System.Collections.IEnumerable items and not string
                ? string.Join("|", items.Cast<object?>())
                : Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }

        foreach (var spanEvent in span.Events)
        {
            foreach (var tag in spanEvent.Tags)
            {
                yield return Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            }
        }
    }
}
