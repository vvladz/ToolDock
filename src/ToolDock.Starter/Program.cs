using ToolDock.Common;
using ToolDock.Common.Logging;
using ToolDock.Starter.Pipes;
using ToolDock.Starter.Supervisor;

namespace ToolDock.Starter;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var attached = ConsoleHost.TryAttachParent();
        var paths = new ToolDockPaths();
        paths.EnsureDirectories();
        using var log = new ToolDockLog(attached, Path.Combine(paths.Logs, "starter.log"));

        if (args is ["--help" or "-h"])
        {
            log.Info("Usage: starter.exe [start|stop|restart|status <tool>]");
            return 0;
        }

        if (args.Length != 0)
        {
            return await RunClientAsync(args, log);
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
                if (attached)
                {
                    log.Warning("starter is already running");
                }
                return 0;
            }

            log.Info("starter initialized");
            return await RunServerAsync(paths, log, attached);
        }
        catch (Exception exception)
        {
            log.Error("starter failed", exception);
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

    private static async Task<int> RunClientAsync(string[] args, ILog log)
    {
        if (args.Length != 2 || args[0] is not ("start" or "stop" or "restart" or "status"))
        {
            log.Error("Usage: starter.exe [start|stop|restart|status <tool>]");
            return 2;
        }

        try
        {
            Validation.ValidateToolName(args[1]);
            var response = await new StarterClient().SendAsync(
                $"{args[0]} {args[1]}",
                TimeSpan.FromSeconds(5));
            if (response.StartsWith("OK", StringComparison.Ordinal))
            {
                log.Info(response);
                return 0;
            }

            log.Error(response);
            return 1;
        }
        catch (Exception exception)
        {
            log.Error("starter is unavailable", exception);
            return 1;
        }
    }

    private static async Task<int> RunServerAsync(ToolDockPaths paths, ILog log, bool attached)
    {
        using var cancellation = new CancellationTokenSource();
        if (attached)
        {
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
        }

        await using var supervisor = new ToolSupervisor(paths, log);
        await supervisor.AutostartAsync(cancellation.Token);
        var server = new PipeServer(supervisor, log);
        await server.RunAsync(cancellation.Token);
        return 0;
    }
}
