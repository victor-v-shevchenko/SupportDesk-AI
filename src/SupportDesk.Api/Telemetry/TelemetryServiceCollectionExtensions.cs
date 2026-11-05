using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SupportDesk.Core.Telemetry;

namespace SupportDesk.Api.Telemetry;

/// <summary>The composition root for tracing and metrics (PLAN.md 14).</summary>
public static class TelemetryServiceCollectionExtensions
{
    public const string EndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";
    public const string ServiceNameVariable = "OTEL_SERVICE_NAME";
    public const string DefaultServiceName = "supportdesk-api";

    /// <summary>
    /// Registers the telemetry options, and tracing with the OTLP exporter only when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is
    /// set. Without it no tracer provider exists, so nothing listens to the sources and nothing is exported.
    /// </summary>
    public static IServiceCollection AddSupportDeskTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<TelemetryOptions>()
            .BindConfiguration(TelemetryOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<TelemetryOptions>, TelemetryOptionsValidator>();
        services.AddHostedService<TelemetryContentWarning>();
        // The counters exist whether or not anything listens; with no listener adding to one is a no-op
        services.AddMetrics();
        services.TryAddSingleton<SupportDeskMetrics>();

        if (!string.IsNullOrWhiteSpace(configuration[EndpointVariable]))
        {
            var serviceName = configuration[ServiceNameVariable];
            services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService(string.IsNullOrWhiteSpace(serviceName) ? DefaultServiceName : serviceName))
                .WithTracing(tracing => tracing.AddSupportDeskSources().AddOtlpExporter())
                .WithMetrics(metrics => metrics.AddSupportDeskMeters().AddOtlpExporter());
        }

        return services;
    }

    /// <summary>
    /// The sources and instrumentation, without an exporter: the app's spans and Microsoft.Extensions.AI's, ASP.NET Core
    /// (not the polled health endpoint), outgoing HTTP and Npgsql. Tests add an in-memory exporter after it.
    /// </summary>
    public static TracerProviderBuilder AddSupportDeskSources(this TracerProviderBuilder tracing)
    {
        ArgumentNullException.ThrowIfNull(tracing);

        return tracing
            .AddSource(SupportDeskTelemetry.SourceName, SupportDeskTelemetry.AiSourceName)
            .AddAspNetCoreInstrumentation(options =>
                options.Filter = context => !context.Request.Path.StartsWithSegments(HealthPath, StringComparison.OrdinalIgnoreCase))
            .AddHttpClientInstrumentation()
            .AddNpgsql()
            // Before any exporter, so every exporter sees the cleaned span
            .AddProcessor<SpanSanitizer>();
    }

    /// <summary>
    /// The meters, without an exporter: the app's (<c>SupportDesk</c>), Microsoft.Extensions.AI's (its token usage and operation
    /// duration histograms) and ASP.NET Core's built-in ones (request duration, active requests). The export interval is the
    /// SDK's 60 seconds unless <c>OTEL_METRIC_EXPORT_INTERVAL</c> says otherwise.
    /// </summary>
    public static MeterProviderBuilder AddSupportDeskMeters(this MeterProviderBuilder metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        return metrics
            .AddMeter(SupportDeskMetrics.MeterName, SupportDeskTelemetry.AiSourceName)
            .AddAspNetCoreInstrumentation();
    }

    private const string HealthPath = "/api/health";
}
