using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using SupportDesk.Api.Telemetry;

namespace SupportDesk.UnitTests.Telemetry;

/// <summary>A log record as the exporter saw it, copied when it was exported, since the SDK reuses <see cref="LogRecord"/> objects.</summary>
internal sealed record ExportedLog(
    string Category,
    LogLevel Level,
    EventId EventId,
    string? Body,
    string? FormattedMessage,
    IReadOnlyDictionary<string, string?> Attributes,
    IReadOnlyList<string> Scopes,
    string? ExceptionText,
    ActivityTraceId TraceId,
    ActivitySpanId SpanId)
{
    /// <summary>Every string the record carries: body, message, attributes (names and values), scopes and exception.</summary>
    public IEnumerable<string> AllText => new[] { Body, FormattedMessage, ExceptionText }
        .Concat(Attributes.SelectMany(a => new[] { a.Key, a.Value }))
        .Concat(Scopes)
        .OfType<string>();
}

internal sealed class CapturingLogExporter : BaseExporter<LogRecord>
{
    private readonly List<ExportedLog> _records = [];

    public IReadOnlyList<ExportedLog> Records
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
            var attributes = new Dictionary<string, string?>();
            foreach (var attribute in record.Attributes ?? [])
            {
                attributes[attribute.Key] = Convert.ToString(attribute.Value, System.Globalization.CultureInfo.InvariantCulture);
            }

            var copy = new ExportedLog(
                record.CategoryName ?? "", record.LogLevel, record.EventId, record.Body, record.FormattedMessage, attributes, scopes,
                record.Exception?.ToString(), record.TraceId, record.SpanId);
            lock (_records)
            {
                _records.Add(copy);
            }
        }

        return ExportResult.Success;
    }
}

/// <summary>
/// A local OTLP/HTTP endpoint. The real exporter posts protobuf to it, so a test can look for a marker in the bytes that would
/// leave the machine, not just in what a processor saw.
/// </summary>
internal sealed class OtlpSink : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<byte[]> _bodies = [];
    private readonly Task _loop;

    public OtlpSink(HttpStatusCode status = HttpStatusCode.OK)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Endpoint = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Endpoint + "/");
        _listener.Start();
        _loop = Task.Run(() => RunAsync(status));
    }

    public string Endpoint { get; }

    public int Requests
    {
        get
        {
            lock (_bodies)
            {
                return _bodies.Count;
            }
        }
    }

    public bool Received(string text)
    {
        var needle = Encoding.UTF8.GetBytes(text);
        lock (_bodies)
        {
            return _bodies.Any(body => body.AsSpan().IndexOf(needle) >= 0);
        }
    }

    public void Dispose()
    {
        _listener.Close();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The listener was closed under the loop
        }
    }

    private async Task RunAsync(HttpStatusCode status)
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            using var body = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(body);
            lock (_bodies)
            {
                _bodies.Add(body.ToArray());
            }

            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "application/x-protobuf";
            context.Response.Close();
        }
    }

    /// <summary>The settings that point the OTLP exporter at this sink over HTTP.</summary>
    public Dictionary<string, string?> Settings() => new()
    {
        ["OTEL_EXPORTER_OTLP_ENDPOINT"] = Endpoint,
        ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
    };
}

/// <summary>
/// The app's log export (<c>AddSupportDeskTelemetry</c>) with the real OTLP exporter pointed at an <see cref="OtlpSink"/>, and a
/// capturing exporter added after it, so <see cref="Logs"/> are the records the OTLP exporter was given.
/// </summary>
internal sealed class LogCapture : IDisposable
{
    private readonly ServiceProvider _services;

    public LogCapture(LogLevel level = LogLevel.Information, bool captureContent = false, HttpStatusCode sinkStatus = HttpStatusCode.OK, Action<IServiceCollection>? configure = null)
    {
        Sink = new OtlpSink(sinkStatus);
        var settings = Sink.Settings();
        settings["Telemetry:CaptureMessageContent"] = captureContent ? "true" : "false";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = "Development", ApplicationName = "SupportDesk.Api" });
        services.AddLogging(builder => builder.SetMinimumLevel(level).AddProvider(Console));
        configure?.Invoke(services);
        services.AddSupportDeskTelemetry(configuration);
        services.ConfigureOpenTelemetryLoggerProvider(builder => builder.AddProcessor(new SimpleLogRecordExportProcessor(Exporter)));
        _services = services.BuildServiceProvider();
    }

    public OtlpSink Sink { get; }

    /// <summary>What a console-like provider next to the export sees: every line, whatever the export drops.</summary>
    public CategoryLoggerProvider Console { get; } = new();

    public CapturingLogExporter Exporter { get; } = new();

    public IReadOnlyList<ExportedLog> Logs => Exporter.Records;

    public IServiceProvider Services => _services;

    public ILogger<T> Logger<T>() => _services.GetRequiredService<ILogger<T>>();

    /// <summary>Sends what the batch exporter holds to the sink, and waits for the sink to have it.</summary>
    public void Flush()
    {
        Assert.True(_services.GetRequiredService<LoggerProvider>().ForceFlush(10_000), "The log export didn't finish.");
    }

    public void Dispose()
    {
        _services.Dispose();
        Sink.Dispose();
    }
}

/// <summary>Keeps every log line with its category, level and text, as a console provider would see it.</summary>
internal sealed class CategoryLoggerProvider : ILoggerProvider
{
    private readonly List<(string Category, LogLevel Level, string Text)> _lines = [];

    public IReadOnlyList<(string Category, LogLevel Level, string Text)> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
        // Nothing to release
    }

    private sealed class Logger(CategoryLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._lines)
            {
                owner._lines.Add((category, logLevel, formatter(state, exception) + " " + exception));
            }
        }
    }
}
