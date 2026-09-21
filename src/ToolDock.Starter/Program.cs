using Microsoft.Extensions.Logging;
using ToolDock.Common;
using ToolDock.Common.Logging;
using ToolDock.Starter.Pipes;
using ToolDock.Starter.Supervisor;

namespace ToolDock.Starter;

internal static class Program
{
    public static int Main(string[] args)
    {
        var paths = new ToolDockPaths();
        paths.EnsureDirectories();
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddProvider(new RotatingFileLoggerProvider(Path.Combine(paths.Logs, "starter.log"))));
        var log = loggerFactory.CreateLogger("ToolDock.Starter");

        if (args.Length != 0)
        {
            log.LogError("ToolDock.Starter does not accept arguments");
            return 2;
        }

        using var mutex = new Mutex(initiallyOwned: false, @"Local\ToolDock.Starter");
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            if (!ownsMutex)
            {
                return 0;
            }

            log.LogInformation("Starter initialized");
            return RunServerAsync(paths, loggerFactory, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            log.LogError(exception, "Starter failed");
            return 1;
        }
        finally
        {
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static async Task<int> RunServerAsync(
        ToolDockPaths paths,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        await using var supervisor = new ToolSupervisor(
            paths,
            loggerFactory.CreateLogger<ToolSupervisor>());
        await supervisor.AutostartAsync(cancellationToken);
        var server = new PipeServer(supervisor, loggerFactory.CreateLogger<PipeServer>());
        await server.RunAsync(cancellationToken);
        return 0;
    }
}
