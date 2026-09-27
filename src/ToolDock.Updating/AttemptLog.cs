using System.Text;
using Microsoft.Extensions.Logging;

namespace ToolDock.Updating;

// Captures only the current attempt, without timestamps or historical file contents.
internal sealed class AttemptLog(ILoggerFactory inner) : ILoggerFactory
{
    private readonly Queue<string> _lines = new();
    private readonly object _gate = new();
    public void Clear() { lock (_gate) _lines.Clear(); }
    public string Text { get { lock (_gate) return string.Join('\n', _lines); } }

    private void Append(string text)
    {
        lock (_gate)
        {
            foreach (var line in text.Replace("\r", "").Split('\n'))
            {
                var bounded = line;
                while (Encoding.UTF8.GetByteCount(bounded) > 4000) bounded = bounded[..^1];
                _lines.Enqueue(bounded);
                while (_lines.Count > 20 || Encoding.UTF8.GetByteCount(string.Join('\n', _lines)) > 4096)
                    _lines.Dequeue();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(inner.CreateLogger(categoryName), Append);
    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
    public void Dispose() { }

    private sealed class CaptureLogger(ILogger inner, Action<string> append) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel level) => inner.IsEnabled(level);
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (level >= LogLevel.Information) append(formatter(state, exception) + (exception is null ? "" : "\n" + exception.Message));
            inner.Log(level, id, state, exception, formatter);
        }
    }
}
