using Microsoft.Extensions.Options;
using SupportDesk.Core.Telemetry;

namespace SupportDesk.Api.Telemetry;

/// <summary>Says at startup, at warning level, when spans and exported logs will hold prompt and ticket text (the debug setting CLAUDE.md allows).</summary>
public sealed partial class TelemetryContentWarning(IOptions<TelemetryOptions> options, ILogger<TelemetryContentWarning> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.CaptureMessageContent)
        {
            LogCapturing();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telemetry:CaptureMessageContent is on: prompts, replies and tool arguments are put in traces, and log lines that hold such text are exported. Use it for debugging only")]
    private partial void LogCapturing();
}
