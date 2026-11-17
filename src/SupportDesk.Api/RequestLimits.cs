using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SupportDesk.Api.Endpoints;
using SupportDesk.Core.AI.Usage;

namespace SupportDesk.Api;

/// <summary><c>RateLimiting:Ai</c>: the fixed window on the AI endpoints (PLAN.md 5).</summary>
public sealed class AiRateLimitOptions
{
    public const string SectionName = "RateLimiting:Ai";

    /// <summary>AI requests the whole app accepts per window.</summary>
    [Range(1, 100_000)]
    public int PermitLimit { get; set; } = 20;

    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;
}

/// <summary>
/// The limits on the endpoints that call a model or an embedding API (PLAN.md 5, 6.4): a rate limit on all of them and,
/// on the ones that write <c>ModelCalls</c> rows, the daily spending limit.
/// </summary>
public static class RequestLimits
{
    public const string AiPolicy = "ai";

    /// <summary>
    /// One fixed window for the whole app, not per client: with no sign-in the only key would be an IP address, which the
    /// web container's proxy hides and a forwarded header lets a client choose. The window caps how fast the key is spent.
    /// </summary>
    public static IServiceCollection AddSupportDeskRequestLimits(this IServiceCollection services)
    {
        services.AddOptions<AiRateLimitOptions>()
            .BindConfiguration(AiRateLimitOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<DailySpendLimit>();
        services.AddRateLimiter(limiter =>
        {
            limiter.AddPolicy(AiPolicy, context =>
            {
                var options = context.RequestServices.GetRequiredService<IOptions<AiRateLimitOptions>>().Value;
                return RateLimitPartition.GetFixedWindowLimiter(AiPolicy, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.PermitLimit,
                    Window = TimeSpan.FromSeconds(options.WindowSeconds),
                    QueueLimit = 0,
                });
            });
            limiter.OnRejected = WriteRejectedAsync;
        });
        return services;
    }

    /// <summary>The rate limit and the daily spending limit, for an endpoint that writes <c>ModelCalls</c> rows.</summary>
    public static RouteHandlerBuilder WithModelSpendLimits(this RouteHandlerBuilder endpoint) =>
        endpoint.RequireRateLimiting(AiPolicy).AddEndpointFilter<DailySpendLimitFilter>();

    private static async ValueTask WriteRejectedAsync(OnRejectedContext rejected, CancellationToken cancellationToken)
    {
        if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            SetRetryAfter(rejected.HttpContext, retryAfter);
        }

        await ModelProblems.RateLimited().ExecuteAsync(rejected.HttpContext);
    }

    internal static void SetRetryAfter(HttpContext context, TimeSpan wait) =>
        context.Response.Headers.RetryAfter = ((long)Math.Ceiling(Math.Max(wait.TotalSeconds, 1))).ToString(CultureInfo.InvariantCulture);
}

/// <summary>Refuses the request before its handler runs when today's model spend has reached the limit.</summary>
public sealed partial class DailySpendLimitFilter(DailySpendLimit limit, TimeProvider time, ILogger<DailySpendLimitFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var status = await limit.CheckAsync(context.HttpContext.RequestAborted);
        if (!status.IsReached)
        {
            return await next(context);
        }

        LogLimitReached(status.SpentUsd, status.LimitUsd);
        RequestLimits.SetRetryAfter(context.HttpContext, status.ResetsAt - time.GetUtcNow());
        return ModelProblems.DailyLimitReached();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused an AI request: today's model spend is {SpentUsd} USD, at or over the daily limit of {LimitUsd} USD")]
    private partial void LogLimitReached(decimal spentUsd, decimal limitUsd);
}
