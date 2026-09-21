using Microsoft.Extensions.Logging;

namespace ToolDock.Common.Logging;

public sealed class TerminalLoggerProvider : ILoggerProvider
{
    private readonly object _sync = new();

    public ILogger CreateLogger(string categoryName) => new TerminalLogger(_sync);

    public void Dispose()
    {
    }

    private sealed class TerminalLogger(object sync) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var writer = logLevel >= LogLevel.Error ? Console.Error : Console.Out;
            lock (sync)
            {
                writer.WriteLine(formatter(state, exception));
                if (exception is not null)
                {
                    writer.WriteLine(exception.Message);
                }
            }
        }
    }
}
