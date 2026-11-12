using System.Globalization;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting;

namespace ToolDock;

internal sealed class Logging : IDisposable
{
    private readonly SerilogLoggerFactory factory;
    internal Microsoft.Extensions.Logging.ILogger Logger { get; }
    internal Logging(Paths paths, bool console, long size = Settings.LogSize, int count = Settings.LogCount)
    {
        Directory.CreateDirectory(paths.LogDirectory);
        var formatter = new LineFormatter();
        var config = new LoggerConfiguration().Enrich.FromLogContext().Enrich.WithProperty("Origin", "runner")
            .WriteTo.File(formatter, Path.Combine(paths.LogDirectory, "log-.log"), rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: size, rollOnFileSizeLimit: true, retainedFileCountLimit: count, shared: false);
        if (console) config.WriteTo.Console(formatter);
        factory = new SerilogLoggerFactory(config.CreateLogger(), dispose: true);
        Logger = factory.CreateLogger("ToolDock");
    }
    public void Dispose() => factory.Dispose();
    private sealed class LineFormatter : ITextFormatter
    {
        public void Format(LogEvent logEvent, TextWriter output)
        {
            var origin = logEvent.Properties.TryGetValue("Origin", out var property) && property is ScalarValue value ? value.Value : "runner";
            output.Write($"{logEvent.Timestamp.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ} [{origin}] ");
            output.WriteLine(logEvent.RenderMessage(CultureInfo.InvariantCulture));
            if (logEvent.Exception is not null) output.WriteLine(logEvent.Exception.Message);
        }
    }
}
