using AgentManager.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AgentManager.App;

/// <summary>Sends Blazor's and the WebView's own warnings and errors (an exception in a component) to the app log.</summary>
public sealed class AppLogProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
    {
        return new Logger(categoryName);
    }

    public void Dispose()
    {
    }

    private sealed class Logger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= LogLevel.Warning;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = $"{category}: {formatter(state, exception)}";
            if (logLevel >= LogLevel.Error)
            {
                AppLog.Error("blazor", message, exception);
            }
            else
            {
                AppLog.Warn("blazor", message);
            }
        }
    }
}
