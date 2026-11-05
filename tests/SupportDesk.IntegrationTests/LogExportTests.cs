using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using SupportDesk.Api.Endpoints;
using SupportDesk.Api.Telemetry;

namespace SupportDesk.IntegrationTests;

/// <summary>
/// What a request leaves in the exported logs (PLAN.md 14): the API in memory with the app's log export
/// (<c>WithSupportDeskLogging</c>, so its processor, wrapper and options) and tracing, both into memory.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public class LogExportTests(PostgresFixture postgres)
{
    // Debug for the app's own categories, and ASP.NET Core's request lines (Information), so a request writes several lines
    private static readonly Dictionary<string, string?> Settings = new()
    {
        ["Retrieval:MinSimilarity"] = "0",
        ["Logging:LogLevel:SupportDesk"] = "Debug",
        ["Logging:LogLevel:Microsoft.AspNetCore"] = "Information",
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_reply_on_T_1002_exports_log_lines_that_all_carry_the_requests_trace_id()
    {
        var spans = new List<Activity>();
        var exporter = new CapturingExporter();
        using var console = new LogCollector();
        await using var api = await StartAsync(spans, exporter, console);
        var ticketId = await TicketIdAsync(api, "T-1002");

        using var response = await api.Client.PostAsync(new Uri($"/api/tickets/{ticketId}/reply", UriKind.Relative), null, Ct);
        await response.Content.ReadAsStringAsync(Ct);

        var server = await WaitForAsync(() => spans.ToList().FirstOrDefault(a => a.Kind == ActivityKind.Server && a.DisplayName.Contains("/reply", StringComparison.Ordinal)));
        var path = $"/api/tickets/{ticketId}/reply";
        // The request's own lines: ASP.NET Core wrote them, with the request path in a scope. Without any, the check below would be empty
        await WaitForAsync(() => exporter.Records.FirstOrDefault(r => r.Scopes.Contains($"RequestPath={path}")));
        var ofRequest = exporter.Records.Where(r => r.Scopes.Contains($"RequestPath={path}")).ToList();
        Assert.NotEmpty(ofRequest);
        Assert.All(ofRequest, r => Assert.Equal(server.TraceId, r.TraceId));
        Assert.Contains(ofRequest, r => r.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_classification_on_a_ticket_with_a_marker_body_leaves_the_marker_and_the_raw_model_output_out_of_every_exported_record()
    {
        const string RawOutput = "Sure! Here is the classification";
        var marker = $"BODYMARKER-{Guid.NewGuid():N}";
        var spans = new List<Activity>();
        var exporter = new CapturingExporter();
        using var console = new LogCollector();
        await using var api = await StartAsync(spans, exporter, console);
        using var created = await api.Client.PostAsJsonAsync(
            new Uri("/api/tickets", UriKind.Relative), new CreateTicketRequest($"Subject {marker}", $"Body {marker}", "Ana Silva", null), JsonDefaults.Options, Ct);
        created.EnsureSuccessStatusCode();
        var markerTicket = (await created.Content.ReadFromJsonAsync<TicketDetailResponse>(JsonDefaults.Options, Ct))!.Id;
        var rejectedTicket = await TicketIdAsync(api, "T-1024");

        using var first = await api.Client.PostAsync(new Uri($"/api/tickets/{markerTicket}/classification", UriKind.Relative), null, Ct);
        using var second = await api.Client.PostAsync(new Uri($"/api/tickets/{rejectedTicket}/classification", UriKind.Relative), null, Ct);
        await first.Content.ReadAsStringAsync(Ct);
        await second.Content.ReadAsStringAsync(Ct);

        // T-1024's scenario answers with rejected text: the console log has the debug line, the export has not
        Assert.Contains(RawOutput, console.All, StringComparison.Ordinal);
        var classification = await WaitForAsync(() => spans.ToList().FirstOrDefault(a => a.OperationName == "classification" && Equals(a.GetTagItem("supportdesk.ticket_id"), rejectedTicket)));
        var lines = await WaitForAsync(() => exporter.Records.Where(r => r.Category.EndsWith(".ClassificationService", StringComparison.Ordinal) && r.TraceId == classification.TraceId).ToList() is { Count: > 0 } found ? found : null);
        // The classification's lines are inside its trace, under its own span, and the content line isn't among them
        Assert.All(lines, r => Assert.Equal(classification.SpanId, r.SpanId));
        Assert.Contains(lines, r => r.Text.Contains("needs manual classification", StringComparison.Ordinal));
        Assert.DoesNotContain(exporter.Records.SelectMany(r => r.AllText), text => text.Contains(RawOutput, StringComparison.Ordinal));
        Assert.DoesNotContain(exporter.Records.SelectMany(r => r.AllText), text => text.Contains(marker, StringComparison.Ordinal));
        Assert.DoesNotContain(exporter.Records, r => r.EventId.Id == SupportDesk.Core.Telemetry.ContentLogEvents.ClassificationRejected);
    }

    private Task<SeededApi> StartAsync(List<Activity> spans, CapturingExporter exporter, ILoggerProvider console) =>
        SeededApi.StartAsync(
            postgres,
            Ct,
            Settings,
            services => services.AddOpenTelemetry()
                .WithTracing(tracing => tracing.AddSupportDeskSources().AddInMemoryExporter(spans))
                .WithSupportDeskLogging(logging => logging.AddProcessor(new SimpleLogRecordExportProcessor(exporter))),
            console);

    private static async Task<int> TicketIdAsync(SeededApi api, string number)
    {
        await using var db = api.CreateDbContext();
        return await db.Tickets.Where(t => t.Number == number).Select(t => t.Id).SingleAsync(Ct);
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> find)
        where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            if (find() is { } found)
            {
                return found;
            }

            Assert.True(DateTime.UtcNow < deadline, "Nothing matching turned up.");
            await Task.Delay(50, Ct);
        }
    }

    /// <summary>A record as exported, copied at export because the SDK reuses the objects.</summary>
    private sealed record Exported(string Category, EventId EventId, string Text, IReadOnlyList<string> Scopes, IReadOnlyList<string> Attributes, string? Exception, ActivityTraceId TraceId, ActivitySpanId SpanId)
    {
        public IEnumerable<string> AllText => new[] { Text, Exception }.OfType<string>().Concat(Scopes).Concat(Attributes);
    }

    private sealed class CapturingExporter : BaseExporter<LogRecord>
    {
        private readonly List<Exported> _records = [];

        public IReadOnlyList<Exported> Records
        {
            get
            {
                lock (_records)
                {
                    return [.. _records];
                }
            }
        }

        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (var record in batch)
            {
                var scopes = new List<string>();
                record.ForEachScope(
                    static (scope, list) =>
                    {
                        foreach (var item in scope)
                        {
                            list.Add($"{item.Key}={item.Value}");
                        }
                    },
                    scopes);
                var attributes = (record.Attributes ?? []).Select(a => $"{a.Key}={a.Value}").ToList();
                var copy = new Exported(
                    record.CategoryName ?? "", record.EventId, record.FormattedMessage ?? record.Body ?? "", scopes, attributes,
                    record.Exception?.ToString(), record.TraceId, record.SpanId);
                lock (_records)
                {
                    _records.Add(copy);
                }
            }

            return ExportResult.Success;
        }
    }
}
