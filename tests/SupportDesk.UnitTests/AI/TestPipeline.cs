using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Usage;

namespace SupportDesk.UnitTests.AI;

/// <summary>The real pipeline around a test provider, with settings, a clock and a sink that keeps the rows.</summary>
internal sealed class TestPipeline : IDisposable
{
    private readonly ServiceProvider _services;

    private TestPipeline(ServiceProvider services, IChatClient client, RecordingSink rows)
    {
        _services = services;
        Client = client;
        Rows = rows;
    }

    public IChatClient Client { get; }

    public RecordingSink Rows { get; }

    public static TestPipeline Create(
        IChatClient provider,
        TimeProvider? time = null,
        IModelErrorClassifier? classifier = null,
        Action<AiOptions>? configure = null,
        ILoggerProvider? logger = null,
        IModelCallSink? sink = null)
    {
        var rows = new RecordingSink();
        var services = new ServiceCollection()
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
            .AddSingleton(Options.Create(Settings(configure)))
            .BuildServiceProvider();

        return new TestPipeline(services, AiPipeline.Build(provider, classifier, services), rows);
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
