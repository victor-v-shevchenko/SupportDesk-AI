using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportDesk.Core.Telemetry;

namespace SupportDesk.Core.AI.Usage;

/// <summary>Where <see cref="UsageLoggingMiddleware"/> hands its rows; adding one never waits.</summary>
public interface IModelCallSink
{
    void Add(ModelCall row);
}

/// <summary>
/// A bounded queue between the pipeline and the database writer, so a slow or failing database can't slow a stream.
/// When it's full, the new row is dropped with a warning.
/// </summary>
public sealed partial class ModelCallBuffer : IModelCallSink
{
    private readonly Channel<ModelCall> _channel;
    private readonly SupportDeskMetrics _metrics;
    private readonly ILogger<ModelCallBuffer> _logger;
    private volatile bool _closed;

    public ModelCallBuffer(IOptions<AiOptions> options, SupportDeskMetrics metrics, ILogger<ModelCallBuffer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Wait mode, so TryWrite says when it's full; nothing ever waits, since only TryWrite is used
        _channel = Channel.CreateBounded<ModelCall>(new BoundedChannelOptions(options.Value.Usage.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
        _metrics = metrics;
        _logger = logger;
    }

    public ChannelReader<ModelCall> Reader => _channel.Reader;

    public void Add(ModelCall row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (_channel.Writer.TryWrite(row))
        {
            return;
        }

        if (_closed)
        {
            _metrics.RecordDropped(MetricValues.DropShutdown, 1);
            LogDroppedAfterClose(row.Feature, row.TicketId);
        }
        else
        {
            _metrics.RecordDropped(MetricValues.DropBufferFull, 1);
            LogDropped(row.Feature, row.TicketId);
        }
    }

    /// <summary>Stops taking rows; the writer drains what's left.</summary>
    public void Complete()
    {
        _closed = true;
        _channel.Writer.TryComplete();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The model call buffer is full; dropped the {Feature} row for ticket {TicketId}")]
    private partial void LogDropped(AiFeature feature, int? ticketId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The app is shutting down; dropped the {Feature} row for ticket {TicketId}")]
    private partial void LogDroppedAfterClose(AiFeature feature, int? ticketId);
}
