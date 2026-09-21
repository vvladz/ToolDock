using Microsoft.Extensions.Logging;
using ToolDock.Common;
using ToolDock.Common.Logging;
using ToolDock.Starter.Pipes;
using ToolDock.Starter.Supervisor;

namespace ToolDock.Starter;

internal static class Program
{
    private const string Usage = "Usage: starter.exe list | starter.exe start|stop|restart|status <tool>";

    public static int Main(string[] args)
    {
        var attached = ConsoleHost.TryAttachParent();
        var paths = new ToolDockPaths();
        paths.EnsureDirectories();
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddProvider(new RotatingFileLoggerProvider(Path.Combine(paths.Logs, "starter.log"))));
        var log = loggerFactory.CreateLogger("ToolDock.Starter");

        if (args is ["--help" or "-h"])
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (args.Length != 0)
        {
            return RunClientAsync(args, log).GetAwaiter().GetResult();
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
                    Console.Error.WriteLine("starter is already running");
                }
                return 0;
            }

            log.LogInformation("Starter initialized");
            return RunServerAsync(paths, loggerFactory, attached).GetAwaiter().GetResult();
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

    private static async Task<int> RunClientAsync(string[] args, ILogger log)
    {
        string request;
        if (args is ["list"])
        {
            request = "list";
        }
        else if (args is [var command, var name] &&
                 command is "start" or "stop" or "restart" or "status")
        {
            request = $"{command} {name}";
        }
        else
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            if (args.Length == 2)
            {
                Validation.ValidateToolName(args[1]);
            }
            var response = await new StarterClient().SendAsync(
                request,
                TimeSpan.FromSeconds(5));
            if (response.StartsWith("OK", StringComparison.Ordinal))
            {
                Console.WriteLine(response);
                return 0;
            }

            Console.Error.WriteLine(response);
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"starter is unavailable: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunServerAsync(
        ToolDockPaths paths,
        ILoggerFactory loggerFactory,
        bool attached)
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

        await using var supervisor = new ToolSupervisor(
            paths,
            loggerFactory.CreateLogger<ToolSupervisor>());
        await supervisor.AutostartAsync(cancellation.Token);
        var server = new PipeServer(supervisor, loggerFactory.CreateLogger<PipeServer>());
        await server.RunAsync(cancellation.Token);
        return 0;
    }
}
