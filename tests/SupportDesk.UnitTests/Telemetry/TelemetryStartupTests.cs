using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SupportDesk.Api.Telemetry;
using SupportDesk.UnitTests.AI;

namespace SupportDesk.UnitTests.Telemetry;

/// <summary>The telemetry registrations on their own (PLAN.md 14): the Production check, and what exists with and without a collector.</summary>
[Collection(FakeClockGroup.Name)]
public class TelemetryStartupTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings, string environment = "Development", ILoggerProvider? logger = null) =>
        BuildCollection(settings, environment, logger).BuildServiceProvider();

    private static ServiceCollection BuildCollection(Dictionary<string, string?> settings, string environment = "Development", ILoggerProvider? logger = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = environment, ApplicationName = "SupportDesk.Api" });
        services.AddLogging(builder =>
        {
            if (logger is not null)
            {
                builder.AddProvider(logger);
            }
        });
        services.AddSupportDeskTelemetry(configuration);
        return services;
    }

    [Fact]
    public void Capturing_message_content_fails_startup_in_Production_and_names_the_setting()
    {
        using var services = Build(new() { ["Telemetry:CaptureMessageContent"] = "true" }, "Production");

        var error = Assert.Throws<OptionsValidationException>(services.ValidateOnStart);

        Assert.Contains("Telemetry:CaptureMessageContent", Assert.Single(error.Failures), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Development", "true")]
    [InlineData("Production", "false")]
    [InlineData("Production", null)]
    public void Other_combinations_start(string environment, string? capture)
    {
        using var services = Build(new() { ["Telemetry:CaptureMessageContent"] = capture }, environment);

        services.ValidateOnStart();

        Assert.Equal(capture == "true", services.GetRequiredService<IOptions<SupportDesk.Core.Telemetry.TelemetryOptions>>().Value.CaptureMessageContent);
    }

    [Fact]
    public async Task Capturing_message_content_is_logged_at_warning_level_when_the_app_starts()
    {
        using var logs = new CapturingLoggerProvider();
        using var services = Build(new() { ["Telemetry:CaptureMessageContent"] = "true" }, logger: logs);

        foreach (var hosted in services.GetServices<IHostedService>())
        {
            await hosted.StartAsync(TestContext.Current.CancellationToken);
        }

        Assert.Contains(logs.Messages, m => m.Contains("CaptureMessageContent is on", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_an_endpoint_no_tracer_provider_exists_so_nothing_listens_or_exports()
    {
        using var services = Build([]);

        Assert.Null(services.GetService<TracerProvider>());
    }

    [Fact]
    public void A_blank_endpoint_counts_as_unset()
    {
        // docker-compose.yml passes an empty value when the variable isn't set
        using var services = Build(new() { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "" });

        Assert.Null(services.GetService<TracerProvider>());
    }

    [Fact]
    public void With_an_endpoint_the_tracer_provider_listens_to_the_app_and_the_ai_sources()
    {
        using var services = Build(new() { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317" });

        using var provider = Assert.IsAssignableFrom<TracerProvider>(services.GetService<TracerProvider>());
        using var app = new System.Diagnostics.ActivitySource("SupportDesk");
        using var ai = new System.Diagnostics.ActivitySource("Experimental.Microsoft.Extensions.AI");
        Assert.True(app.HasListeners());
        Assert.True(ai.HasListeners());
    }

    [Fact]
    public void Without_an_endpoint_no_meter_provider_exists_so_nothing_is_exported()
    {
        using var services = Build([]);

        Assert.Null(services.GetService<MeterProvider>());
        // The counters still exist, for the code that records to them
        Assert.NotNull(services.GetRequiredService<SupportDesk.Core.Telemetry.SupportDeskMetrics>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Without_an_endpoint_no_OpenTelemetry_logger_provider_is_registered(string? endpoint)
    {
        using var services = Build(new() { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint }, logger: new CapturingLoggerProvider());

        Assert.Null(services.GetService<OpenTelemetry.Logs.LoggerProvider>());
        Assert.DoesNotContain(services.GetServices<ILoggerProvider>(), IsOpenTelemetryProvider);
    }

    [Fact]
    public void With_an_endpoint_one_OpenTelemetry_logger_provider_is_registered_next_to_the_others()
    {
        using var services = Build(new() { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317" }, logger: new CapturingLoggerProvider());

        Assert.NotNull(services.GetService<OpenTelemetry.Logs.LoggerProvider>());
        // The wrapper is what the container hands out; the SDK's own provider is only inside it
        var providers = services.GetServices<ILoggerProvider>().ToList();
        Assert.IsType<ContentLogFilteringProvider>(Assert.Single(providers, IsOpenTelemetryProvider));
        Assert.DoesNotContain(providers, p => p.GetType().Name == "OpenTelemetryLoggerProvider");
        Assert.Contains(services.GetServices<ILoggerProvider>(), p => p is CapturingLoggerProvider);
    }

    [Fact]
    public void Log_export_fails_at_startup_when_the_OpenTelemetry_provider_was_registered_before_it_could_be_wrapped()
    {
        // An earlier registration (Aspire service defaults, builder.Logging.AddOpenTelemetry) makes WithLogging's TryAddEnumerable
        // add nothing, so there is no provider to wrap and content events would be exported unfiltered
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddOpenTelemetry());

        var error = Assert.Throws<InvalidOperationException>(
            () => services.AddOpenTelemetry().WithSupportDeskLogging(_ => { }));

        Assert.Contains("found 0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_export_fails_at_startup_when_OpenTelemetry_logging_is_registered_again_after_it()
    {
        // A later registration (builder.Logging.AddOpenTelemetry, service defaults) adds a second provider that isn't wrapped,
        // so content events would reach the export through it
        var services = BuildCollection(new() { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317" });
        services.AddLogging(logging => logging.AddOpenTelemetry());
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(provider.ValidateOnStart);

        Assert.Contains("OpenTelemetry logger provider", Assert.Single(error.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void Log_export_fails_at_startup_when_it_is_set_up_twice()
    {
        // Each call wraps its own provider, so every record would be exported twice
        var services = BuildCollection(new() { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317" });
        services.AddOpenTelemetry().WithSupportDeskLogging(_ => { });
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(provider.ValidateOnStart);
    }

    [Fact]
    public void Log_export_set_up_once_passes_the_startup_check()
    {
        using var services = Build(new() { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317" });

        services.ValidateOnStart();
    }

    private static bool IsOpenTelemetryProvider(ILoggerProvider provider) =>
        provider.GetType().GetCustomAttributes(typeof(ProviderAliasAttribute), inherit: false).Cast<ProviderAliasAttribute>().Any(a => a.Alias == "OpenTelemetry");

    [Fact]
    public void A_blank_endpoint_has_no_meter_provider_either()
    {
        using var services = Build(new() { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "" });

        Assert.Null(services.GetService<MeterProvider>());
    }

    [Theory]
    [InlineData("SupportDesk")]
    [InlineData("Experimental.Microsoft.Extensions.AI")]
    [InlineData("Microsoft.AspNetCore.Hosting")]
    public void With_an_endpoint_the_meter_provider_listens_to_the_apps_the_ai_and_the_aspnet_core_meters(string meterName)
    {
        using var services = Build(new() { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317" });

        using var provider = Assert.IsAssignableFrom<MeterProvider>(services.GetService<MeterProvider>());
        using var meter = new System.Diagnostics.Metrics.Meter(meterName);
        var counter = meter.CreateCounter<int>("test");
        Assert.True(counter.Enabled);
    }

    [Fact]
    public void A_meter_nobody_asked_for_is_not_listened_to()
    {
        using var services = Build(new() { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317" });

        using var provider = Assert.IsAssignableFrom<MeterProvider>(services.GetService<MeterProvider>());
        using var meter = new System.Diagnostics.Metrics.Meter("SomethingElse");
        Assert.False(meter.CreateCounter<int>("test").Enabled);
    }
}
