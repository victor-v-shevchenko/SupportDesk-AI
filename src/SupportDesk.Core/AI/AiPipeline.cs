using Microsoft.Extensions.AI;

namespace SupportDesk.Core.AI;

/// <summary>The one chat pipeline every feature uses (PLAN.md 6.2), built around the configured provider.</summary>
public static class AiPipeline
{
    public const int MaxToolIterations = 3;

    /// <summary>Wraps <paramref name="provider"/>, outermost first. Step 3b adds the safeguards in the marked places.</summary>
    public static IChatClient Build(IChatClient provider, IModelErrorClassifier? errorClassifier, IServiceProvider? services = null) =>
        new ChatClientBuilder(provider)
            // Step 3b: RequestCapMiddleware
            .UseFunctionInvocation(configure: client => client.MaximumIterationsPerRequest = MaxToolIterations)
            // Step 3b: BudgetGuardMiddleware, UsageLoggingMiddleware, RetryTimeoutMiddleware (it retries on ModelUnavailableException.IsTransient)
            .Use(inner => new ErrorMappingMiddleware(inner, errorClassifier))
            .Build(services);
}
