using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OpenTelemetry;
using OpenTelemetry.Logs;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Prompts;
using SupportDesk.Core.Classification;
using SupportDesk.Core.Telemetry;
using SupportDesk.Core.Tickets;
using SupportDesk.UnitTests.AI;
using SupportDesk.UnitTests.Classification;

namespace SupportDesk.UnitTests.Telemetry;

/// <summary>
/// The log export (PLAN.md 14) through the app's own wiring and the real OTLP exporter, pointed at a local sink: what a record
/// carries, that exception text and content-bearing lines never leave, and that exporting doesn't log.
/// </summary>
[Collection(FakeClockGroup.Name)]
public sealed partial class LogExportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Marker(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    [Fact]
    public void A_log_carries_its_template_placeholders_as_attributes_the_formatted_message_the_category_and_the_request_scope()
    {
        using var capture = new LogCapture();
        var logger = capture.Logger<LogExportTests>();
        var runId = Guid.NewGuid();

        using (logger.BeginScope(new Dictionary<string, object> { ["RequestPath"] = "/api/tickets/7/reply" }))
        {
            Finished(logger, runId, 7, 3);
        }

        var log = Assert.Single(capture.Logs, l => l.Attributes.GetValueOrDefault("ReplyRunId") == runId.ToString());
        Assert.Equal("7", log.Attributes["TicketId"]);
        Assert.Equal("3", log.Attributes["Count"]);
        Assert.Equal($"Reply {runId} for ticket 7 finished with 3 chunks", log.FormattedMessage);
        Assert.Equal("Reply {ReplyRunId} for ticket {TicketId} finished with {Count} chunks", log.Attributes["{OriginalFormat}"]);
        Assert.Equal(LogLevel.Information, log.Level);
        Assert.Equal(typeof(LogExportTests).FullName, log.Category);
        Assert.Contains("RequestPath=/api/tickets/7/reply", log.Scopes);
    }

    [Fact]
    public void A_log_written_inside_a_span_has_that_spans_trace_and_span_ids_and_one_outside_has_none()
    {
        using var traces = new TraceCapture();
        using var capture = new LogCapture();
        var logger = capture.Logger<LogExportTests>();
        var inside = Guid.NewGuid();
        var outside = Guid.NewGuid();

        using (var span = SupportDeskTelemetry.Source.StartActivity("reply"))
        {
            Assert.NotNull(span);
            Finished(logger, inside, 1, 1);
            var log = Assert.Single(capture.Logs, l => l.Attributes.GetValueOrDefault("ReplyRunId") == inside.ToString());
            Assert.Equal(span.TraceId, log.TraceId);
            Assert.Equal(span.SpanId, log.SpanId);
            Assert.NotEqual(traces.TraceId, default);
        }

        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            Finished(logger, outside, 1, 1);
        }
        finally
        {
            Activity.Current = previous;
        }

        var unlinked = Assert.Single(capture.Logs, l => l.Attributes.GetValueOrDefault("ReplyRunId") == outside.ToString());
        Assert.Equal(default, unlinked.TraceId);
        Assert.Equal(default, unlinked.SpanId);
    }

    [Fact]
    public void An_exception_is_exported_as_its_type_and_its_innermost_type_with_no_message_or_stack_trace()
    {
        var marker = Marker("EXCMARKER");
        using var capture = new LogCapture();
        var exception = new InvalidOperationException($"outer {marker}", new FormatException($"inner {marker}", new TimeoutException($"innermost {marker}")));

        Failed(capture.Logger<LogExportTests>(), exception, 7);
        capture.Flush();

        var log = Assert.Single(capture.Logs, l => l.Attributes.ContainsKey("exception.type"));
        Assert.Equal("System.InvalidOperationException", log.Attributes["exception.type"]);
        Assert.Equal("System.TimeoutException", log.Attributes["supportdesk.inner_exception.type"]);
        Assert.Null(log.ExceptionText);
        Assert.DoesNotContain(log.AllText, text => text.Contains(marker, StringComparison.Ordinal));
        // What the OTLP exporter wrote: the types are there, the message and the stack trace are not
        Assert.True(capture.Sink.Received("System.InvalidOperationException"));
        Assert.True(capture.Sink.Received("System.TimeoutException"));
        Assert.False(capture.Sink.Received(marker));
        Assert.False(capture.Sink.Received("at SupportDesk"));
    }

    [Fact]
    public void An_exception_without_an_inner_exception_has_no_inner_type()
    {
        using var capture = new LogCapture();
        var marker = Marker("EXCNOINNER");

        Failed(capture.Logger<LogExportTests>(), new InvalidOperationException(marker), 8);

        var log = Assert.Single(capture.Logs, l => l.Attributes.ContainsKey("exception.type"));
        Assert.False(log.Attributes.ContainsKey("supportdesk.inner_exception.type"));
    }

    [Fact]
    public void Without_the_sanitizer_the_exception_message_and_stack_trace_do_reach_the_export()
    {
        // The positive control: the SDK's own export of the same log, so the test above can't pass because nothing is written
        var marker = Marker("EXCCONTROL");
        using var sink = new OtlpSink();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(sink.Settings()).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddOpenTelemetry().WithLogging(logging => logging.AddOtlpExporter(), options => options.IncludeFormattedMessage = true);
        using var provider = services.BuildServiceProvider();

        Failed(provider.GetRequiredService<ILogger<LogExportTests>>(), new InvalidOperationException($"outer {marker}"), 7);
        Assert.True(provider.GetRequiredService<LoggerProvider>().ForceFlush(10_000));

        Assert.True(sink.Received(marker));
        Assert.True(sink.Received("exception.stacktrace"));
    }

    [Fact]
    public async Task A_rejected_classifications_raw_output_is_not_exported_unless_content_capture_is_on_and_the_console_still_gets_it()
    {
        var marker = Marker("RAWMARKER");
        using var capture = new LogCapture(LogLevel.Debug);
        using var console = new CapturingLoggerProvider();
        capture.Services.GetRequiredService<ILoggerFactory>().AddProvider(console);

        await ClassifyWithRejectedOutputAsync(capture, marker);
        capture.Flush();

        // The line is written (the console provider has it), the export doesn't
        Assert.Contains(console.Messages, m => m.Contains(marker, StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Logs, l => l.EventId.Id == ContentLogEvents.ClassificationRejected);
        Assert.DoesNotContain(capture.Logs.SelectMany(l => l.AllText), text => text.Contains(marker, StringComparison.Ordinal));
        Assert.False(capture.Sink.Received(marker));
        // The classification's other lines are exported, so the filter isn't dropping everything
        Assert.Contains(capture.Logs, l => l.Level == LogLevel.Information && l.FormattedMessage!.Contains("needs manual classification", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rejected_classifications_raw_output_is_exported_when_content_capture_is_on()
    {
        var marker = Marker("RAWCAPTURE");
        using var capture = new LogCapture(LogLevel.Debug, captureContent: true);

        await ClassifyWithRejectedOutputAsync(capture, marker);
        capture.Flush();

        // Both rejected answers
        var rejected = capture.Logs.Where(l => l.EventId.Id == ContentLogEvents.ClassificationRejected).ToList();
        Assert.Equal(2, rejected.Count);
        Assert.All(rejected, l => Assert.Contains(marker, l.FormattedMessage, StringComparison.Ordinal));
        Assert.True(capture.Sink.Received(marker));
    }

    [Fact]
    public void Only_the_classification_rejection_is_in_the_content_set()
    {
        // A new log call that takes prompt, ticket, reply or tool text joins the set (and this list) in the same change
        Assert.Equal([ContentLogEvents.ClassificationRejected], ContentLogEvents.Ids);
        Assert.True(ContentLogEvents.IsContent(new EventId(ContentLogEvents.ClassificationRejected)));
        Assert.False(ContentLogEvents.IsContent(default));
    }

    [Fact]
    public void An_idle_app_with_export_on_writes_no_log_record_of_its_own()
    {
        using var capture = new LogCapture(LogLevel.Trace);
        var marker = Guid.NewGuid();

        var logger = capture.Logger<LogExportTests>();
        Finished(logger, marker, 1, 1);
        capture.Flush();
        var requests = capture.Sink.Requests;
        Thread.Sleep(1_500);
        capture.Flush();

        // The one record we wrote was exported (a request reached the sink); sending it made no record, and idling made none
        Assert.True(requests >= 1);
        Assert.Equal(requests, capture.Sink.Requests);
        var log = Assert.Single(capture.Logs);
        Assert.Equal(marker.ToString(), log.Attributes["ReplyRunId"]);
        Assert.Equal(typeof(LogExportTests).FullName, log.Category);
    }

    [Fact]
    public void A_failing_export_writes_no_log_record_either()
    {
        using var capture = new LogCapture(LogLevel.Trace, sinkStatus: HttpStatusCode.ServiceUnavailable);
        var marker = Guid.NewGuid();

        var logger = capture.Logger<LogExportTests>();
        Finished(logger, marker, 1, 1);
        capture.Flush();
        Thread.Sleep(1_500);

        Assert.True(capture.Sink.Requests >= 1);
        Assert.Single(capture.Logs, l => l.Attributes.GetValueOrDefault("ReplyRunId") == marker.ToString());
        Assert.Single(capture.Logs);
    }

    private static async Task ClassifyWithRejectedOutputAsync(LogCapture capture, string marker)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        var tickets = new ClassificationServiceTests.InMemoryTickets();
        var ticket = tickets.Add("T-1");
        var provider = new ScriptedChatClient(
            time,
            ScriptedChatClient.Steps(ScriptedChatClient.Text($"not json {marker}")),
            ScriptedChatClient.Steps(ScriptedChatClient.Text($"still not json {marker}")));
        using var pipeline = TestPipeline.Create(provider, time);
        using var metrics = new TestMetrics();
        var prompts = new PromptStore(Options.Create(new AiOptions
        {
            PromptsPath = Path.Combine(AppContext.BaseDirectory, "prompts"),
            Prompts = new Dictionary<string, string> { ["classify"] = "classify.v2" },
        }));
        var service = new ClassificationService(pipeline.Client, prompts, tickets, metrics.Metrics, time, capture.Logger<ClassificationService>());

        var result = await service.ClassifyAsync(ticket.Id, Ct);

        Assert.Equal(ClassificationStatus.NeedsManual, result?.Status);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reply {ReplyRunId} for ticket {TicketId} finished with {Count} chunks")]
    private static partial void Finished(ILogger logger, Guid replyRunId, int ticketId, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected failure for ticket {TicketId}")]
    private static partial void Failed(ILogger logger, Exception exception, int ticketId);
}
