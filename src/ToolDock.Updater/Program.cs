using Microsoft.Extensions.Logging;
using ToolDock.Common;
using ToolDock.Common.Logging;
using ToolDock.Updating;

namespace ToolDock.Updater;

internal static class Program
{
    public static int Main(string[] args)
    {
        var paths = new ToolDockPaths();
        paths.EnsureDirectories();
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddProvider(new RotatingFileLoggerProvider(Path.Combine(paths.Logs, "updater.log"))));
        var log = loggerFactory.CreateLogger("ToolDock.Updater");

        if (args.Length != 0)
        {
            log.LogError("ToolDock.Updater does not accept arguments");
            return 2;
        }

        var result = new UpdateCoordinator(paths, loggerFactory).Run();
        return result.Status switch
        {
            UpdateStatus.Completed or UpdateStatus.AlreadyRunning => 0,
            UpdateStatus.Cancelled => 130,
            _ => 1
        };
    }
}
