using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Usage;
using SupportDesk.Core.Telemetry;
using SupportDesk.UnitTests.Telemetry;

namespace SupportDesk.UnitTests.AI;

/// <summary>The real pipeline around a test provider, with settings, a clock and a sink that keeps the rows.</summary>
internal sealed class TestPipeline : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly bool _ownsMetrics;

    private TestPipeline(ServiceProvider services, IChatClient client, RecordingSink rows, TestMetrics metrics, bool ownsMetrics)
    {
        Metrics = metrics;
        _ownsMetrics = ownsMetrics;
        _services = services;
        Client = client;
        Rows = rows;
    }

    public IChatClient Client { get; }

    public RecordingSink Rows { get; }

    /// <summary>The counters the pipeline (and any service built over it) records to.</summary>
    public TestMetrics Metrics { get; }

    public static TestPipeline Create(
        IChatClient provider,
        TimeProvider? time = null,
        IModelErrorClassifier? classifier = null,
        Action<AiOptions>? configure = null,
        ILoggerProvider? logger = null,
        IModelCallSink? sink = null,
        bool captureMessageContent = false,
        TestMetrics? metrics = null,
        ILoggerFactory? loggerFactory = null)
    {
        var rows = new RecordingSink();
        var ownsMetrics = metrics is null;
        metrics ??= new TestMetrics();
        var collection = new ServiceCollection()
            .AddLogging(builder =>
            {
                if (logger is not null)
                {
                    builder.AddProvider(logger);
                }
            })
            .AddSingleton(time ?? TimeProvider.System)
            .AddSingleton(sink ?? rows)
            .AddSingleton<PriceTable>()
            .AddSingleton(metrics.Metrics)
            .AddSingleton(Options.Create(Settings(configure)))
            .AddSingleton(Options.Create(new TelemetryOptions { CaptureMessageContent = captureMessageContent }));
        if (loggerFactory is not null)
        {
            // The caller's factory replaces the container's own, so a test sees what the pipeline's clients log
            collection.AddSingleton(loggerFactory);
        }

        var services = collection.BuildServiceProvider();

        return new TestPipeline(services, AiPipeline.Build(provider, classifier, services), rows, metrics, ownsMetrics);
    }

    /// <summary>The code defaults, the prices from appsettings.json that tests rely on, and the test's changes.</summary>
    public static AiOptions Settings(Action<AiOptions>? configure = null)
    {
        var options = new AiOptions
        {
            Pricing =
            [
                new ModelPrice { Model = "claude-opus-5-5", InputPerMTok = 4.00m, OutputPerMTok = 20.00m },
                new ModelPrice { Model = "fake", InputPerMTok = 0m, OutputPerMTok = 0m },
            ],
        };
        configure?.Invoke(options);
        return options;
    }

    public void Dispose()
    {
        Client.Dispose();
        if (_ownsMetrics)
        {
            Metrics.Dispose();
        }

        _services.Dispose();
    }
}

internal sealed class RecordingSink : IModelCallSink
{
    private readonly List<ModelCall> _rows = [];

    public IReadOnlyList<ModelCall> All
    {
        get
        {
            lock (_rows)
            {
                return [.. _rows];
            }
        }
    }

    public void Add(ModelCall row)
    {
        lock (_rows)
        {
            _rows.Add(row);
        }
    }
}
