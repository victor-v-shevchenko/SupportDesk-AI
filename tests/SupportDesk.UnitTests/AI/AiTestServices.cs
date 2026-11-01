using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportDesk.Api.AI;

namespace SupportDesk.UnitTests.AI;

/// <summary>The API's AI registrations on their own, with in-memory settings and no web host.</summary>
internal static class AiTestServices
{
    /// <summary>What appsettings.json sets for prompts and the Ollama endpoint; everything else uses the code defaults unless a test overrides it.</summary>
    private static readonly Dictionary<string, string?> BaseSettings = new()
    {
        ["AI:Prompts:reply"] = "reply.v1",
        ["AI:Prompts:summary"] = "summary.v2",
        ["AI:Prompts:classify"] = "classify.v1",
        ["AI:Ollama:Endpoint"] = "http://ollama.test:11434",
    };

    public static ServiceProvider Build(IReadOnlyDictionary<string, string?> settings, string environment = "Development", ILoggerProvider? logger = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(BaseSettings)
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = environment, ApplicationName = "SupportDesk.Api" });
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            if (logger is not null)
            {
                builder.AddProvider(logger);
            }
        });
        services.AddSupportDeskAi();
        return services.BuildServiceProvider();
    }

    /// <summary>Runs the checks the host runs at startup (<c>ValidateOnStart</c>).</summary>
    public static void ValidateOnStart(this IServiceProvider services) =>
        services.GetRequiredService<IStartupValidator>().Validate();

    /// <summary>The startup error for <paramref name="settings"/>.</summary>
    public static OptionsValidationException StartupError(IReadOnlyDictionary<string, string?> settings, string environment = "Development")
    {
        using var services = Build(settings, environment);
        return Assert.Throws<OptionsValidationException>(services.ValidateOnStart);
    }
}

/// <summary>Collects every log message, to check what never gets logged.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_messages)
            {
                return [.. _messages];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
        // Nothing to release
    }

    private sealed class Logger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._messages)
            {
                owner._messages.Add(formatter(state, exception) + " " + exception);
            }
        }
    }
}
