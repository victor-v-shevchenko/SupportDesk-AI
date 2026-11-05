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
    public async Task A_classifications_lines_are_selected_by_their_request_and_sit_in_the_classification_span_and_the_raw_output_stays_out()
    {
        const string RawOutput = "Sure! Here is the classification";
        var spans = new List<Activity>();
        var exporter = new CapturingExporter();
        using var console = new LogCollector();
        await using var api = await StartAsync(spans, exporter, console);
        var rejectedTicket = await TicketIdAsync(api, "T-1024");
        var path = $"/api/tickets/{rejectedTicket}/classification";

        using var response = await api.Client.PostAsync(new Uri(path, UriKind.Relative), null, Ct);
        await response.Content.ReadAsStringAsync(Ct);

        // T-1024's scenario answers with rejected text: the console log has the debug line, the export has not
        Assert.Contains(RawOutput, console.All, StringComparison.Ordinal);
        var classification = await WaitForAsync(() => spans.ToList().FirstOrDefault(a => a.OperationName == "classification" && Equals(a.GetTagItem("supportdesk.ticket_id"), rejectedTicket)));
        // Chosen by the request that wrote them (the path scope ASP.NET Core adds), not by the trace they claim
        var lines = await WaitForAsync(() => exporter.Records.Where(r => r.Category.EndsWith(".ClassificationService", StringComparison.Ordinal) && r.Scopes.Contains($"RequestPath={path}")).ToList() is { Count: > 0 } found ? found : null);
        Assert.All(lines, r => Assert.Equal((classification.TraceId, classification.SpanId), (r.TraceId, r.SpanId)));
        Assert.Contains(lines, r => r.Text.Contains("needs manual classification", StringComparison.Ordinal));
        Assert.DoesNotContain(exporter.Records.SelectMany(r => r.AllText), text => text.Contains(RawOutput, StringComparison.Ordinal));
        Assert.DoesNotContain(exporter.Records, r => r.EventId.Id == SupportDesk.Core.Telemetry.ContentLogEvents.ClassificationRejected);
    }

    [Fact]
    public async Task A_search_query_stays_out_of_every_exported_record_while_the_request_lines_are_still_exported()
    {
        var marker = $"SEARCHMARKER-{Guid.NewGuid():N}";
        var spans = new List<Activity>();
        var exporter = new CapturingExporter();
        using var console = new LogCollector();
        await using var api = await StartAsync(spans, exporter, console);

        using var response = await api.Client.GetAsync(new Uri($"/api/search?q={marker}", UriKind.Relative), Ct);
        await response.Content.ReadAsStringAsync(Ct);

        var server = await WaitForAsync(() => spans.ToList().FirstOrDefault(a => a.Kind == ActivityKind.Server && a.DisplayName.Contains("/api/search", StringComparison.Ordinal)));
        var lines = await WaitForAsync(() => exporter.Records.Where(r => r.Scopes.Contains("RequestPath=/api/search")).ToList() is { Count: >= 2 } found ? found : null);
        // The console-like log has the raw query (the control), the export has the lines with the query redacted
        Assert.Contains(marker, console.All, StringComparison.Ordinal);
        Assert.All(lines, r => Assert.Equal(server.TraceId, r.TraceId));
        Assert.Contains(lines, r => r.Text.StartsWith("Request starting", StringComparison.Ordinal) && r.Text.Contains("/api/search?Redacted", StringComparison.Ordinal));
        Assert.Contains(lines, r => r.Text.StartsWith("Request finished", StringComparison.Ordinal));
        await Task.Delay(300, Ct);
        Assert.DoesNotContain(exporter.Records.SelectMany(r => r.AllText), text => text.Contains(marker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_ef_query_leaves_the_database_error_text_out_of_every_exported_record()
    {
        var marker = $"QRYMARKER-{Guid.NewGuid():N}";
        var spans = new List<Activity>();
        var exporter = new CapturingExporter();
        using var console = new LogCollector();
        await using var api = await StartAsync(spans, exporter, console);

        // A cast that fails: PostgreSQL's message quotes the value ('invalid input syntax for type integer: "..."'), and EF Core
        // logs the exception as a template value (QueryIterationFailed: "...{newline}{error}")
        using (var scope = api.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SupportDesk.Data.SupportDeskDbContext>();
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.SqlQuery<int>($"SELECT {marker}::int AS \"Value\"").ToListAsync(Ct));
        }

        // The control: EF logged the failure with the text in it, and the export has the record without it
        Assert.Contains(marker, console.All, StringComparison.Ordinal);
        var failure = await WaitForAsync(() => exporter.Records.FirstOrDefault(r => r.Category == "Microsoft.EntityFrameworkCore.Query" && r.Attributes.Any(a => a.StartsWith("error=", StringComparison.Ordinal) && !a.Contains(marker, StringComparison.Ordinal))));
        Assert.DoesNotContain(exporter.Records.SelectMany(r => r.AllText), text => text.Contains(marker, StringComparison.Ordinal));
        Assert.DoesNotContain(failure.Text, "invalid input syntax", StringComparison.Ordinal);
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
