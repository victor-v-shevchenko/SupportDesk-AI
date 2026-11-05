using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SupportDesk.Core.Telemetry;

namespace SupportDesk.Api.Telemetry;

/// <summary>The composition root for tracing, metrics and logs (PLAN.md 14).</summary>
public static class TelemetryServiceCollectionExtensions
{
    public const string EndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";
    public const string ServiceNameVariable = "OTEL_SERVICE_NAME";
    public const string DefaultServiceName = "supportdesk-api";

    /// <summary>
    /// Registers the telemetry options, and tracing, metrics and logs with the OTLP exporter only when
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set. Without it no tracer, meter or OpenTelemetry logger provider exists, so
    /// nothing listens to the sources and nothing is exported.
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
                .WithMetrics(metrics => metrics.AddSupportDeskMeters().AddOtlpExporter())
                .WithSupportDeskLogging(logging => logging.AddOtlpExporter());
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
    /// The log export without an exporter, which the caller adds (<c>AddOtlpExporter</c>; tests add an in-memory one): the
    /// OpenTelemetry logger provider next to the console one, with the formatted message, the scopes and the parsed state
    /// values as attributes. <see cref="LogSanitizer"/> is registered before the exporter, since processors run in the order
    /// they are added, and the provider is wrapped so content events never reach it unless
    /// <c>Telemetry:CaptureMessageContent</c> is on.
    /// </summary>
    public static IOpenTelemetryBuilder WithSupportDeskLogging(this IOpenTelemetryBuilder builder, Action<LoggerProviderBuilder> addExporter)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(addExporter);

        var services = builder.Services;
        var before = services.Count;
        builder.WithLogging(
            logging =>
            {
                logging.AddProcessor<LogSanitizer>();
                addExporter(logging);
            },
            options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
                options.ParseStateValues = true;
            });

        // WithLogging registers the provider as a factory, whose type can't be read from the descriptor: wrap what this call
        // added, and fail at startup if that isn't exactly one provider rather than export unfiltered
        var wrapped = 0;
        for (var i = before; i < services.Count; i++)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType == typeof(ILoggerProvider) && descriptor.ImplementationFactory is { } factory)
            {
                services[i] = ServiceDescriptor.Singleton<ILoggerProvider>(sp =>
                {
                    var options = new Lazy<TelemetryOptions>(() => sp.GetRequiredService<IOptions<TelemetryOptions>>().Value);
                    return new ContentLogFilteringProvider((ILoggerProvider)factory(sp), () => options.Value.CaptureMessageContent);
                });
                wrapped++;
            }
        }

        if (wrapped != 1)
        {
            throw new InvalidOperationException($"Expected WithLogging to register one logger provider to wrap, found {wrapped}; OpenTelemetry logging was registered before WithSupportDeskLogging.");
        }

        // A registration after this one, or a second call, adds a provider this loop can't see: checked at startup instead
        LogExportCheck.Register(services);

        return builder;
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
