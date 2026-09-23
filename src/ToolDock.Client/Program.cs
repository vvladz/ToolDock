using System.Diagnostics;
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
          tdctl variable set <name> <value>
          tdctl variable get|remove <name>
          tdctl variable list|status
          tdctl secret set <name> [--stdin]
          tdctl secret remove <name>
          tdctl secret list|status
          tdctl logs <ToolDock.Starter|ToolDock.Updater|daemon> [--lines <count>] [--follow]
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
                ["variable", "set", var name, var value] => SetVariable(name, value),
                ["variable", "get", var name] => GetVariable(name),
                ["variable", "remove", var name] => RemoveVariable(name),
                ["variable", "list"] => ListVariables(),
                ["variable", "status"] => ShowValueStatus(secret: false),
                ["secret", "set", var name] => SetSecret(name, readFromStandardInput: false),
                ["secret", "set", var name, "--stdin"] => SetSecret(name, readFromStandardInput: true),
                ["secret", "remove", var name] => RemoveSecret(name),
                ["secret", "list"] => ListSecrets(),
                ["secret", "status"] => ShowValueStatus(secret: true),
                ["exec", var name, "--", .. var commandArguments]
                    => RunCommand(name, commandArguments),
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
            .AddProvider(new RotatingFileLoggerProvider(Path.Combine(paths.Logs, "ToolDock.Updater.log")))
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

    private static int SetVariable(string name, string value)
    {
        var paths = CreatePaths();
        new VariableStore(paths).Set(name, value);
        Console.WriteLine($"Variable set: {name}");
        return 0;
    }

    private static int GetVariable(string name)
    {
        var store = new VariableStore(CreatePaths());
        if (!store.TryGet(name, out var value))
        {
            Console.Error.WriteLine($"Variable is not set: {name}");
            return 1;
        }

        Console.WriteLine(value);
        return 0;
    }

    private static int RemoveVariable(string name)
    {
        var removed = new VariableStore(CreatePaths()).Remove(name);
        Console.WriteLine(removed ? $"Variable removed: {name}" : $"Variable is not set: {name}");
        return 0;
    }

    private static int ListVariables()
    {
        foreach (var name in new VariableStore(CreatePaths()).List())
        {
            Console.WriteLine(name);
        }
        return 0;
    }

    private static int SetSecret(string name, bool readFromStandardInput)
    {
        var value = ReadSecret(readFromStandardInput);
        new SecretStore(CreatePaths()).Set(name, value);
        Console.WriteLine($"Secret set: {name}");
        return 0;
    }

    private static int RemoveSecret(string name)
    {
        var removed = new SecretStore(CreatePaths()).Remove(name);
        Console.WriteLine(removed ? $"Secret removed: {name}" : $"Secret is not set: {name}");
        return 0;
    }

    private static int ListSecrets()
    {
        foreach (var name in new SecretStore(CreatePaths()).List())
        {
            Console.WriteLine(name);
        }
        return 0;
    }

    private static int ShowValueStatus(bool secret)
    {
        var paths = CreatePaths();
        var stored = new HashSet<string>(
            secret ? new SecretStore(paths).List() : new VariableStore(paths).List(),
            StringComparer.OrdinalIgnoreCase);
        var referenced = ReadReferences(paths, secret);
        var names = new HashSet<string>(stored, StringComparer.OrdinalIgnoreCase);
        names.UnionWith(referenced);

        foreach (var name in names.Order(StringComparer.OrdinalIgnoreCase))
        {
            var status = referenced.Contains(name)
                ? stored.Contains(name) ? "set" : "missing"
                : "unused";
            Console.WriteLine($"{name} {status}");
        }
        return 0;
    }

    private static HashSet<string> ReadReferences(ToolDockPaths paths, bool secret)
    {
        var catalog = JsonFiles.ReadOptionalAsync<ToolCatalog>(paths.CatalogCacheFile)
            .GetAwaiter().GetResult();
        if (catalog is null)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        Validation.ValidateCatalog(catalog);
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var catalogVariables = new HashSet<string>(catalog.Variables.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var package in catalog.Tools.Values)
        {
            var environments = package.Commands.Values.Select(command => command.Environment)
                .Concat(package.Daemons.Values.Select(daemon => daemon.Environment));
            foreach (var environment in environments)
            {
                foreach (var value in environment.Values)
                {
                    var reference = secret ? value.Secret : value.Variable;
                    if (reference is not null && (secret || !catalogVariables.Contains(reference)))
                    {
                        references.Add(reference);
                    }
                }
            }
        }
        return references;
    }

    private static string ReadSecret(bool readFromStandardInput)
    {
        if (readFromStandardInput)
        {
            return Console.In.ReadLine()
                ?? throw new InvalidDataException("No secret was provided on standard input.");
        }

        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException("Interactive secret input requires a console; use --stdin for redirected input.");
        }

        Console.Write("Enter secret: ");
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return value.ToString();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length != 0)
                {
                    value.Length--;
                    Console.Write("\b \b");
                }
                continue;
            }
            if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
                Console.Write('*');
            }
        }
    }

    private static int RunCommand(string name, string[] arguments)
    {
        Validation.ValidateToolName(name);
        var paths = CreatePaths();
        var catalog = JsonFiles.ReadRequiredAsync<ToolCatalog>(paths.CatalogCacheFile)
            .GetAwaiter().GetResult();
        Validation.ValidateCatalog(catalog);
        var (packageName, package, command) = Validation.FindCommand(catalog, name);
        if (!package.Enabled)
        {
            throw new InvalidOperationException($"Package is disabled: {packageName}");
        }

        var state = JsonFiles.ReadRequiredAsync<InstalledState>(paths.InstalledStateFile)
            .GetAwaiter().GetResult();
        var installed = Validation.FindInstalled(state, packageName);
        var packageRoot = paths.ResolveUnderRoot(
            installed.Root ?? Path.Combine("tools", packageName, installed.Version));
        var executable = ResolveUnder(
            packageRoot,
            Validation.ValidateExecutablePath($"command {name}", command.Executable));
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                $"Installed executable is missing: {Path.GetRelativePath(paths.Root, executable)}",
                executable);
        }

        var environment = new ProcessEnvironmentBuilder(paths).Build(command.Environment, catalog.Variables);
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(
            new RotatingFileLoggerProvider(Path.Combine(paths.Logs, "ToolDock.Client.log"))));
        var log = loggerFactory.CreateLogger("ToolDock.Client");
        log.LogInformation("Executing {Command} from {Package} {Version}", name, packageName, installed.Version);
        foreach (var entry in environment.LogEntries)
        {
            log.LogInformation("{EnvironmentEntry}", entry);
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment.Clear();
        foreach (var (environmentName, value) in environment.Values)
        {
            startInfo.Environment[environmentName] = value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start command: {name}");
        process.WaitForExit();
        log.LogInformation("Command {Command} exited with code {ExitCode}", name, process.ExitCode);
        return process.ExitCode;
    }

    private static ToolDockPaths CreatePaths()
    {
        var paths = new ToolDockPaths();
        paths.EnsureDirectories();
        return paths;
    }

    private static string ResolveUnder(string root, string relativePath)
    {
        var resolved = Path.GetFullPath(relativePath, root);
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Executable path escapes the installed version directory.");
        }
        return resolved;
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
