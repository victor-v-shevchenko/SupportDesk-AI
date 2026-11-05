using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SupportDesk.Core.Telemetry;

namespace SupportDesk.Api.Telemetry;

/// <summary>The composition root for tracing (PLAN.md 14).</summary>
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

        if (!string.IsNullOrWhiteSpace(configuration[EndpointVariable]))
        {
            var serviceName = configuration[ServiceNameVariable];
            services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService(string.IsNullOrWhiteSpace(serviceName) ? DefaultServiceName : serviceName))
                .WithTracing(tracing => tracing.AddSupportDeskSources().AddOtlpExporter());
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

    private const string HealthPath = "/api/health";
}
