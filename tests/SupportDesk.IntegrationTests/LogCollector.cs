using Microsoft.Extensions.Logging;

namespace SupportDesk.IntegrationTests;

/// <summary>Keeps every log message with its exception, for tests that check what the app logged.</summary>
public sealed class LogCollector : ILoggerProvider
{
    private readonly List<string> _messages = [];

    public string All
    {
        get
        {
            lock (_messages)
            {
                return string.Join("\n", _messages);
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
        // Nothing to release
    }

    private sealed class Logger(LogCollector owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._messages)
            {
                owner._messages.Add($"{formatter(state, exception)} {exception}");
            }
        }
    }
}
