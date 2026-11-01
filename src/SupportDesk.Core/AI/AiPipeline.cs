using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportDesk.Core.AI.Usage;

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

        return new ChatClientBuilder(provider)
            .Use(inner => new RequestCapMiddleware(inner))
            .UseFunctionInvocation(configure: client => client.MaximumIterationsPerRequest = MaxToolIterations)
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
