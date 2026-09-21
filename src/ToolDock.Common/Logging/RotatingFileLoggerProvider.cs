using Microsoft.Extensions.Logging;

namespace ToolDock.Common.Logging;

public sealed class RotatingFileLoggerProvider(string path) : ILoggerProvider
{
    private readonly RotatingFileWriter _writer = new(path);

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer);

    public void Dispose() => _writer.Dispose();

    private sealed class FileLogger(string category, RotatingFileWriter writer) : ILogger
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

            var message = formatter(state, exception);
            var suffix = exception is null ? string.Empty : $"{Environment.NewLine}{exception}";
            writer.WriteLine(
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {Level(logLevel)} {category} {message}{suffix}");
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "NON"
        };
    }
}
