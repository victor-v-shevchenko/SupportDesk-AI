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
    public void The_content_rules_name_the_classification_rejection_and_the_librarys_lines_below_debug()
    {
        // A new log call that takes prompt, ticket, reply or tool text joins the rules (and this test) in the same change
        var classification = typeof(ClassificationService).FullName;
        var rejected = new EventId(ContentLogEvents.ClassificationRejected);
        Assert.True(ContentLogEvents.IsContent(classification, LogLevel.Debug, rejected));
        Assert.False(ContentLogEvents.IsContent(classification, LogLevel.Debug, default));
        Assert.False(ContentLogEvents.IsContent("SupportDesk.Other", LogLevel.Debug, rejected));
        Assert.True(ContentLogEvents.IsContent("Microsoft.Extensions.AI.FunctionInvokingChatClient", LogLevel.Trace, default));
        Assert.False(ContentLogEvents.IsContent("Microsoft.Extensions.AI.FunctionInvokingChatClient", LogLevel.Debug, default));
        Assert.False(ContentLogEvents.IsContent(null, LogLevel.Trace, rejected));
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

    [Fact]
    public void An_exception_given_as_a_template_argument_is_exported_as_its_type_and_the_body_is_the_template()
    {
        // EF Core's SaveChangesFailed and QueryIterationFailed log "...{newline}{error}" with the exception as a value, not as
        // the log call's exception, so its ToString() would be in the formatted message and in an attribute
        var marker = Marker("ARGMARKER");
        using var capture = new LogCapture();
        var logger = capture.Logger<LogExportTests>();

        var failure = new InvalidOperationException($"duplicate key value (x)=({marker})", new FormatException($"inner {marker}"));
        EfShaped(logger, Environment.NewLine, failure);
        capture.Flush();

        var log = Assert.Single(capture.Logs, l => l.Attributes.ContainsKey("error"));
        Assert.Equal("System.InvalidOperationException", log.Attributes["error"]);
        Assert.Equal("An exception occurred in the database while saving changes.{newline}{error}", log.Body);
        Assert.Null(log.FormattedMessage);
        Assert.DoesNotContain(log.AllText, text => text.Contains(marker, StringComparison.Ordinal));
        Assert.False(capture.Sink.Received(marker));
        Assert.True(capture.Sink.Received("System.InvalidOperationException"));
    }

    [Fact]
    public void A_log_with_both_a_logged_exception_and_an_exception_argument_exports_neither_text()
    {
        var marker = Marker("BOTHMARKER");
        using var capture = new LogCapture();
        var logger = capture.Logger<LogExportTests>();

        BothShaped(logger, new InvalidOperationException($"argument {marker}"), new TimeoutException($"logged {marker}"));
        capture.Flush();

        Assert.False(capture.Sink.Received(marker));
        var log = Assert.Single(capture.Logs, l => l.Attributes.ContainsKey("exception.type"));
        Assert.Equal("System.TimeoutException", log.Attributes["exception.type"]);
        Assert.Equal("System.InvalidOperationException", log.Attributes["Argument"]);
        Assert.Null(log.FormattedMessage);
    }

    [Fact]
    public void A_query_string_is_exported_as_a_fixed_placeholder_and_the_rest_of_the_line_is_kept()
    {
        var marker = Marker("QUERYMARKER");
        using var capture = new LogCapture();
        var logger = capture.Logger<LogExportTests>();

        var query = $"?q={marker}&k=3";
        Starting(logger, "GET", "/api/search", query);
        Starting(logger, "GET", "/api/tickets", "");
        capture.Flush();

        var search = Assert.Single(capture.Logs, l => l.Attributes.GetValueOrDefault("Path") == "/api/search");
        Assert.Equal("?Redacted", search.Attributes["QueryString"]);
        Assert.Equal("Request starting GET /api/search?Redacted", search.FormattedMessage);
        Assert.DoesNotContain(search.AllText, text => text.Contains(marker, StringComparison.Ordinal));
        Assert.False(capture.Sink.Received(marker));
        // No query, nothing to redact
        var plain = Assert.Single(capture.Logs, l => l.Attributes.GetValueOrDefault("Path") == "/api/tickets");
        Assert.Equal("Request starting GET /api/tickets", plain.FormattedMessage);
    }

    [Fact]
    public async Task A_tool_calls_arguments_and_result_logged_at_trace_by_the_library_are_not_exported_unless_content_capture_is_on()
    {
        var marker = Marker("TOOLMARKER");
        using var off = new LogCapture(LogLevel.Trace);
        using var on = new LogCapture(LogLevel.Trace, captureContent: true);

        await RunToolCallAsync(off, marker);
        await RunToolCallAsync(on, marker);
        off.Flush();
        on.Flush();

        // The library wrote the lines (the console-like provider has them) under its own categories
        Assert.Contains(off.Console.Lines, l => l.Category.StartsWith("Microsoft.Extensions.AI", StringComparison.Ordinal) && l.Level == LogLevel.Trace && l.Text.Contains(marker, StringComparison.Ordinal));
        Assert.DoesNotContain(off.Logs.SelectMany(l => l.AllText), text => text.Contains(marker, StringComparison.Ordinal));
        Assert.False(off.Sink.Received(marker));
        Assert.DoesNotContain(off.Logs, l => l.Category.StartsWith("Microsoft.Extensions.AI", StringComparison.Ordinal));
        Assert.Contains(on.Logs.SelectMany(l => l.AllText), text => text.Contains(marker, StringComparison.Ordinal));
        Assert.True(on.Sink.Received(marker));
    }

    [Fact]
    public void The_content_rules_apply_to_their_category_only()
    {
        var marker = Marker("OTHERCAT");
        using var capture = new LogCapture(LogLevel.Trace);

        // The classification rejection's event ID in a category that isn't the classification service's
        var other = capture.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SupportDesk.Other");
        Rejected(other, marker);
        capture.Flush();

        Assert.Single(capture.Logs, l => l.EventId.Id == ContentLogEvents.ClassificationRejected && l.Category == "SupportDesk.Other");
        Assert.True(capture.Sink.Received(marker));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_idle_app_writes_no_log_record_of_its_own_with_an_http_client_factory_registered(bool failing)
    {
        // With IHttpClientFactory registered the OTLP exporter sends through a named client, which logs through ILogger
        // unless exporting is suppressed while it sends (and the exporter filters its category to Warning)
        using var capture = new LogCapture(LogLevel.Trace, sinkStatus: failing ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, configure: services => services.AddHttpClient());
        var marker = Guid.NewGuid();
        var logger = capture.Logger<LogExportTests>();

        Finished(logger, marker, 1, 1);
        capture.Flush();
        Thread.Sleep(1_500);
        capture.Flush();

        Assert.True(capture.Sink.Requests >= 1);
        Assert.Single(capture.Logs);
        // Nor does the exporter's own client log to the other providers while it sends
        Assert.DoesNotContain(capture.Console.Lines, l => l.Category.StartsWith("System.Net.Http", StringComparison.Ordinal) || l.Category.StartsWith("OpenTelemetry", StringComparison.Ordinal));
    }

    private static async Task RunToolCallAsync(LogCapture capture, string marker)
    {
        var time = TimeProvider.System;
        var provider = new ScriptedChatClient(
            time,
            ScriptedChatClient.Steps(ScriptedChatClient.ToolCall("call-1", "lookup", new Dictionary<string, object?> { ["query"] = marker })),
            ScriptedChatClient.Steps(ScriptedChatClient.Text("Done.")));
        using var pipeline = TestPipeline.Create(provider, time, loggerFactory: capture.Services.GetRequiredService<ILoggerFactory>());
        var tool = Microsoft.Extensions.AI.AIFunctionFactory.Create((string query) => $"found {query}", "lookup");

        var response = await pipeline.Client.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Where is it?")],
            new AiCallContext(AiFeature.Reply, "reply.v2", 1, "T-1", Guid.NewGuid()).ApplyTo(new Microsoft.Extensions.AI.ChatOptions { Tools = [tool] }),
            Ct);

        Assert.Equal("Done.", response.Text);
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

    // EF Core's own templates use lower-case placeholders and take the exception as a value, which this copies on purpose
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1727:Use PascalCase for named placeholders", Justification = "Copies EF Core's template, which the test is about")]
    private static readonly Action<ILogger, string, Exception, Exception?> EfShapedMessage =
        LoggerMessage.Define<string, Exception>(LogLevel.Error, new EventId(10000, "SaveChangesFailed"), "An exception occurred in the database while saving changes.{newline}{error}");

    private static void EfShaped(ILogger logger, string newline, Exception error) => EfShapedMessage(logger, newline, error, null);

    private static readonly Action<ILogger, Exception, Exception?> BothShapedMessage =
        LoggerMessage.Define<Exception>(LogLevel.Error, new EventId(10001, "Both"), "Failed with {Argument}");

    private static void BothShaped(ILogger logger, Exception argument, Exception logged) => BothShapedMessage(logger, argument, logged);

    private static readonly Action<ILogger, string, Exception?> RejectedMessage =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(ContentLogEvents.ClassificationRejected, "Rejected"), "Raw output: {Output}");

    private static void Rejected(ILogger logger, string output) => RejectedMessage(logger, output, null);

    [LoggerMessage(Level = LogLevel.Information, Message = "Request starting {Method} {Path}{QueryString}")]
    private static partial void Starting(ILogger logger, string method, string path, string queryString);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected failure for ticket {TicketId}")]
    private static partial void Failed(ILogger logger, Exception exception, int ticketId);
}
