using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Trace;
using SupportDesk.Api.Telemetry;
using SupportDesk.UnitTests.AI;

namespace SupportDesk.UnitTests.Telemetry;

/// <summary>The telemetry registrations on their own (PLAN.md 14): the Production check, and what exists with and without a collector.</summary>
[Collection(FakeClockGroup.Name)]
public class TelemetryStartupTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings, string environment = "Development", ILoggerProvider? logger = null)
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
        return services.BuildServiceProvider();
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
}
