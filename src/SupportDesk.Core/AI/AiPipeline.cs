using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SupportDesk.Core.AI.Usage;
using SupportDesk.Core.Telemetry;

namespace SupportDesk.Core.AI;

/// <summary>The one chat pipeline every feature uses (PLAN.md 6.2), built around the configured provider.</summary>
public static class AiPipeline
{
    public const int MaxToolIterations = 3;

    /// <summary>
    /// Wraps <paramref name="provider"/>, outermost first. <paramref name="services"/> supplies <see cref="IOptions{AiOptions}"/>,
    /// <see cref="TimeProvider"/>, <see cref="IModelCallSink"/>, <see cref="PriceTable"/> and logging.
    /// </summary>
    public static IChatClient Build(IChatClient provider, IModelErrorClassifier? errorClassifier, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.GetRequiredService<IOptions<AiOptions>>().Value;
        var time = services.GetRequiredService<TimeProvider>();
        // Set on the client whatever it is, so OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT can't turn capture on (PLAN.md 14)
        var captureContent = services.GetService<IOptions<TelemetryOptions>>()?.Value.CaptureMessageContent ?? false;

        return new ChatClientBuilder(provider)
            .Use(inner => new RequestCapMiddleware(inner))
            .UseFunctionInvocation(configure: client => client.MaximumIterationsPerRequest = MaxToolIterations)
            // Directly under the tool loop: one chat span per inner call, and the loop's execute_tool spans (it makes them only when a telemetry client is below it)
            // No logger factory: the client would log the failure's exception, whose message can echo the request
            .UseOpenTelemetry(NullLoggerFactory.Instance, SupportDeskTelemetry.AiSourceName, client => client.EnableSensitiveData = captureContent)
            .Use(inner => new TraceTagsMiddleware(inner))
            .Use(inner => new RequestCapCounterMiddleware(inner, options.Budget))
            .Use(inner => new BudgetGuardMiddleware(inner, options.Budget))
            .Use(inner => new UsageLoggingMiddleware(
                inner,
                services.GetRequiredService<IModelCallSink>(),
                services.GetRequiredService<PriceTable>(),
                time,
                services.GetRequiredService<ILogger<UsageLoggingMiddleware>>()))
            .Use(inner => new RetryTimeoutMiddleware(inner, options, time))
            .Use(inner => new ErrorMappingMiddleware(inner, errorClassifier))
            .Build(services);
    }
}
