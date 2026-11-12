using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace ToolDock;

internal static class Program
{
    private const string Help = """
        ToolDock - user daemon runner for Windows

        ToolDock.exe start <daemon>
        ToolDock.exe run <daemon>
        ToolDock.exe stop <daemon>
        ToolDock.exe status <daemon> [--json]
        ToolDock.exe --help
        ToolDock.exe --version
        """;

    private static async Task<int> Main(string[] args)
    {
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
        return await ExecuteAsync(args, cancellation: stopping.Token);
    }

    internal static async Task<int> ExecuteAsync(string[] args, string? root = null, CancellationToken cancellation = default)
    {
        try
        {
            if (args is ["--help"]) { Console.WriteLine(Help); return 0; }
            if (args is ["--version"]) { Console.WriteLine(Assembly.GetExecutingAssembly().GetName().Version); return 0; }
            if (args is ["run", var daemon, "--background", var token, "--root", var backgroundRoot] && Guid.TryParseExact(token, "N", out var runId))
                return await BackgroundAsync(new Paths(daemon, backgroundRoot), runId, cancellation);
            if (args.Length is < 2 or > 3 || args[0] is not ("start" or "run" or "stop" or "status") ||
                (args.Length == 3 && (args[0] != "status" || args[2] != "--json")))
            { Console.Error.WriteLine(Help); return 2; }
            Paths paths;
            try { paths = new Paths(args[1], root); }
            catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }
            switch (args[0])
            {
                case "start": Console.WriteLine(await StartAsync(paths, cancellation)); return 0;
                case "stop": Console.WriteLine(await StopAsync(paths, cancellation)); return 0;
                case "run": return await Runner.RunAsync(paths, Guid.NewGuid(), true,
                    result => { if (!result.Success) Console.Error.WriteLine(result.Message); else if (result.Message == "already running") Console.WriteLine(result.Message); return Task.CompletedTask; }, cancellation);
                default:
                    var status = StateStore.Status(paths);
                    Console.WriteLine(args.Length == 3 ? JsonSerializer.Serialize(status, Settings.Json) :
                        $"{status.Daemon}: {status.Status} (pid {status.Pid?.ToString() ?? "unknown"}, exitCode {status.ExitCode?.ToString() ?? "unknown"})");
                    return 0;
            }
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }

    internal static async Task<string> StartAsync(Paths paths, CancellationToken cancellation = default,
        Func<string, string[], ChildProcess>? launch = null)
    {
        if (StateStore.Active(paths)) return "already running";
        var runId = Guid.NewGuid();
        using var stop = Native.CreateStopEvent(paths.EventName(runId));
        using var pipe = new NamedPipeServerStream("ToolDock.start." + runId.ToString("N"), PipeDirection.In,
            1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(Settings.StartTimeout);
        var executable = Path.Combine(AppContext.BaseDirectory, "ToolDock.exe");
        string[] arguments = ["run", paths.Name, "--background", runId.ToString("N"), "--root", paths.Root];
        using var child = launch is null ? Native.CreateProcess(executable, arguments, detached: true) : launch(executable, arguments);
        try
        {
            child.Resume();
            var connected = pipe.WaitForConnectionAsync(timeout.Token);
            var exited = child.WaitAsync(timeout.Token);
            if (await Task.WhenAny(connected, exited) == exited && !pipe.IsConnected)
                throw new IOException("Background runner exited before connecting.");
            await connected;
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            var line = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Runner did not acknowledge startup.");
            var result = JsonSerializer.Deserialize<StartupResult>(line, Settings.Json) ?? throw new IOException("Invalid startup response.");
            if (!result.Success) throw new InvalidOperationException(result.Message);
            return result.Message;
        }
        catch
        {
            Native.Check(Native.SetEvent(stop));
            using var cleanup = new CancellationTokenSource(Settings.StopTimeout);
            try { await child.WaitAsync(cleanup.Token); }
            catch (OperationCanceledException) { child.Kill(); await child.WaitAsync(); }
            throw;
        }
        finally { timeout.Cancel(); }
    }

    private static async Task<int> BackgroundAsync(Paths paths, Guid runId, CancellationToken token)
    {
        using var pipe = new NamedPipeClientStream(".", "ToolDock.start." + runId.ToString("N"), PipeDirection.Out, PipeOptions.Asynchronous);
        using var connecting = CancellationTokenSource.CreateLinkedTokenSource(token);
        connecting.CancelAfter(Settings.StartTimeout);
        await pipe.ConnectAsync(connecting.Token);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        return await Runner.RunAsync(paths, runId, false, result => writer.WriteLineAsync(JsonSerializer.Serialize(result)), token);
    }

    internal static async Task<string> StopAsync(Paths paths, CancellationToken token = default)
    {
        if (!StateStore.Active(paths)) { _ = StateStore.Read(paths); return "already stopped"; }
        var deadline = DateTime.UtcNow + Settings.StopTimeout;
        Guid? target = null;
        while (StateStore.Active(paths))
        {
            token.ThrowIfCancellationRequested();
            var state = StateStore.Read(paths);
            if (target is not null && state?.RunId != target) return "stopped";
            if (target is null && state is { FinishedAtUtc: null })
            {
                using var stop = Native.OpenStopEvent(paths.EventName(state.RunId));
                if (stop is not null) { Native.Check(Native.SetEvent(stop)); target = state.RunId; }
            }
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Timed out waiting for the runner to stop.");
            await Task.Delay(20, token);
        }
        // A state-write failure during cleanup must not be reported as a successful stop.
        var final = StateStore.Read(paths);
        if (target is not null && final?.RunId == target && final.ExitCode != 0)
            throw new IOException("Runner stopped without a recorded successful result.");
        return "stopped";
    }
}
