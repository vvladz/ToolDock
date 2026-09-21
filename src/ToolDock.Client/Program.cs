using System.Text;
using Microsoft.Extensions.Logging;
using ToolDock.Common;
using ToolDock.Common.Logging;
using ToolDock.Updating;

namespace ToolDock.Client;

internal static class Program
{
    private const string Usage = """
        Usage:
          tdctl list
          tdctl start|stop|restart|status <daemon>
          tdctl update
          tdctl logs <starter|updater|daemon> [--lines <count>] [--follow]
        """;

    public static int Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["--help" or "-h"] => PrintHelp(),
                ["list"] => RunStarterCommandAsync("list").GetAwaiter().GetResult(),
                [var command, var name] when command is "start" or "stop" or "restart" or "status"
                    => RunDaemonCommandAsync(command, name).GetAwaiter().GetResult(),
                ["update"] => RunUpdate(),
                ["logs", .. var logArguments] => ShowLogsAsync(logArguments).GetAwaiter().GetResult(),
                _ => UsageError()
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int PrintHelp()
    {
        Console.WriteLine(Usage);
        return 0;
    }

    private static int UsageError()
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }

    private static async Task<int> RunDaemonCommandAsync(string command, string name)
    {
        Validation.ValidateToolName(name);
        return await RunStarterCommandAsync($"{command} {name}");
    }

    private static async Task<int> RunStarterCommandAsync(string request)
    {
        try
        {
            var response = await new StarterClient().SendAsync(request, TimeSpan.FromSeconds(5));
            var success = response.StartsWith("OK", StringComparison.Ordinal);
            (success ? Console.Out : Console.Error).WriteLine(response);
            return success ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Starter is unavailable: {exception.Message}");
            return 1;
        }
    }

    private static int RunUpdate()
    {
        var paths = new ToolDockPaths();
        paths.EnsureDirectories();
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .AddProvider(new RotatingFileLoggerProvider(Path.Combine(paths.Logs, "updater.log")))
            .AddProvider(new TerminalLoggerProvider()));
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += Cancel;
        try
        {
            var result = new UpdateCoordinator(paths, loggerFactory).Run(cancellation.Token);
            return result.Status switch
            {
                UpdateStatus.Completed => PrintUpdateResult(result, 0),
                UpdateStatus.CompletedWithErrors => PrintUpdateResult(result, 1),
                UpdateStatus.AlreadyRunning => PrintUpdateMessage("Update is already running.", 1),
                UpdateStatus.Cancelled => PrintUpdateMessage("Update cancelled.", 130),
                _ => 1
            };
        }
        finally
        {
            Console.CancelKeyPress -= Cancel;
        }

        void Cancel(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        }
    }

    private static int PrintUpdateResult(UpdateResult result, int exitCode)
    {
        var writer = exitCode == 0 ? Console.Out : Console.Error;
        writer.WriteLine(
            $"Update complete: checked={result.Checked} updated={result.Updated} failed={result.Failed}");
        return exitCode;
    }

    private static int PrintUpdateMessage(string message, int exitCode)
    {
        Console.Error.WriteLine(message);
        return exitCode;
    }

    private static async Task<int> ShowLogsAsync(string[] args)
    {
        if (!TryParseLogArguments(args, out var target, out var lines, out var follow))
        {
            return UsageError();
        }

        Validation.ValidateToolName(target);
        var path = Path.Combine(new ToolDockPaths().Logs, $"{target}.log");
        if (!File.Exists(path) && !follow)
        {
            Console.Error.WriteLine($"Log does not exist: {target}");
            return 1;
        }

        if (File.Exists(path))
        {
            foreach (var line in ReadLastLines(path, lines))
            {
                Console.WriteLine(line);
            }
        }

        if (!follow)
        {
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += Cancel;
        try
        {
            await FollowAsync(path, File.Exists(path) ? new FileInfo(path).Length : 0, cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        finally
        {
            Console.CancelKeyPress -= Cancel;
        }

        void Cancel(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        }
    }

    private static bool TryParseLogArguments(
        string[] args,
        out string target,
        out int lines,
        out bool follow)
    {
        target = args.FirstOrDefault() ?? string.Empty;
        lines = 100;
        follow = false;
        for (var index = 1; index < args.Length; index++)
        {
            if (args[index] is "--follow" or "-f")
            {
                follow = true;
            }
            else if (args[index] == "--lines" &&
                     index + 1 < args.Length &&
                     int.TryParse(args[++index], out var parsedLines) &&
                     parsedLines >= 0)
            {
                lines = parsedLines;
            }
            else
            {
                return false;
            }
        }

        return target.Length != 0;
    }

    private static IReadOnlyCollection<string> ReadLastLines(string path, int count)
    {
        if (count == 0)
        {
            return [];
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = new Queue<string>(count);
        while (reader.ReadLine() is { } line)
        {
            if (lines.Count == count)
            {
                lines.Dequeue();
            }
            lines.Enqueue(line);
        }

        return lines;
    }

    private static async Task FollowAsync(string path, long position, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length < position)
                {
                    position = 0;
                }

                stream.Position = position;
                using var reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    leaveOpen: true);
                var content = await reader.ReadToEndAsync(cancellationToken);
                if (content.Length != 0)
                {
                    Console.Write(content);
                }
                position = stream.Position;
            }

            await Task.Delay(250, cancellationToken);
        }
    }
}
