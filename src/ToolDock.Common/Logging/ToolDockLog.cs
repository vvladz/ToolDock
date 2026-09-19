namespace ToolDock.Common.Logging;

public sealed class ToolDockLog : ILog, IDisposable
{
    private readonly object _consoleSync = new();
    private readonly bool _useConsole;
    private readonly RotatingFileWriter? _file;

    public ToolDockLog(bool useConsole, string filePath)
    {
        _useConsole = useConsole;
        _file = useConsole ? null : new RotatingFileWriter(filePath);
    }

    public void Info(string message) => Write("INF", message);
    public void Warning(string message) => Write("WRN", message);

    public void Error(string message, Exception? exception = null)
        => Write("ERR", exception is null ? message : $"{message}: {exception.Message}");

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level} {message}";
        if (_useConsole)
        {
            lock (_consoleSync)
            {
                Console.WriteLine(line);
            }
        }
        else
        {
            _file!.WriteLine(line);
        }
    }

    public void Dispose() => _file?.Dispose();
}
