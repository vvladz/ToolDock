using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ToolDock;
using Cli = ToolDock.Program;

if (args.FirstOrDefault() == "fixture") return await Fixture(args[1..]);

var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "tools"));
Environment.SetEnvironmentVariable("PATH", AppContext.BaseDirectory + ";" + Environment.GetEnvironmentVariable("PATH"));
var active = new List<Paths>();
var passed = 0;
var detachedSupported = true;
try
{
    await Test("CLI rejects invalid commands", async () => Check(await Cli.ExecuteAsync(["unknown", "x"], root) == 2));
    await Test("list succeeds with no output for missing or empty configuration directories", async () =>
    {
        var missingRoot = Path.Combine(root, "missing-list-root");
        Check(await List(missingRoot) == (0, ""));
        Check(!Directory.Exists(missingRoot), "list must not create directories.");
        Check(await List(root) == (0, ""));
    });
    await Test("list returns sorted configuration names without reading JSON or runtime state", async () =>
    {
        var listRoot = Path.Combine(root, "list-root");
        var tools = Path.Combine(listRoot, "tools");
        Directory.CreateDirectory(Path.Combine(tools, "nested"));
        Directory.CreateDirectory(Path.Combine(tools, "directory.json"));
        File.WriteAllText(Path.Combine(tools, "zeta.json"), "invalid JSON");
        File.WriteAllText(Path.Combine(tools, "Alpha.JSON"), "{}");
        File.WriteAllText(Path.Combine(tools, "middle.name.json"), "null");
        File.WriteAllText(Path.Combine(tools, "ignored.json.bak"), "{}");
        File.WriteAllText(Path.Combine(tools, "nested", "hidden.json"), "{}");
        Directory.CreateDirectory(Path.Combine(listRoot, "state"));
        File.WriteAllText(Path.Combine(listRoot, "state", "stale.json"), "invalid state");
        var (code, output) = await List(listRoot);
        Check(code == 0);
        Check(output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .SequenceEqual(["Alpha", "middle.name", "zeta"]), output);
        Check(!Directory.Exists(Path.Combine(listRoot, "logs")));
    });
    await Test("list rejects unexpected arguments", async () =>
    {
        Check(await Cli.ExecuteAsync(["list", "daemon"], root) == 2);
        Check(await Cli.ExecuteAsync(["list", "--json"], root) == 2);
    });
    await SecretTests.RunAsync(root, Test);
    await Test("argument quoting, environment, working directory and output origins", async () =>
    {
        var result = Path.Combine(root, "arguments.json");
        string[] values = ["", "two words", "a\"b", @"C:\path with spaces\", @"\\server\share", "\u043a\u0438\u0440\u0438\u043b\u0438\u0446\u044f", "{template}"];
        var paths = Config("arguments", ["echo", result, .. values], "never", environment: new() { ["TOOLDOCK_TEST"] = "value", ["PATH"] = "not-the-search-path" });
        Check(await Run(paths) == 0);
        using var document = JsonDocument.Parse(File.ReadAllText(result));
        Check(document.RootElement.GetProperty("args").EnumerateArray().Select(x => x.GetString()).SequenceEqual(values));
        Check(document.RootElement.GetProperty("directory").GetString() == root);
        Check(document.RootElement.GetProperty("environment").GetString() == "value");
        var log = Logs(paths);
        Check(log.Contains("[stdout] out {template}") && log.Contains("[stderr] err {template}"), log);
    });
    await Test("never preserves failed runner result; status command still succeeds", async () =>
    {
        var paths = Config("failed", ["exit", "7"], "never");
        Check(await Run(paths) == 1);
        Check(StateStore.Status(paths) is { Status: "failed", ExitCode: 1 });
        Check(await Cli.ExecuteAsync(["status", paths.Name, "--json"], root) == 0);
    });
    await Test("stdout and stderr drain concurrently without losing lines", async () =>
    {
        var paths = Config("streams", ["burst"], "never");
        Check(await Run(paths) == 0);
        var lines = Logs(paths).Split('\n');
        Check(lines.Count(x => x.Contains("[stdout] output ")) == 1000);
        Check(lines.Count(x => x.Contains("[stderr] error ")) == 1000);
    });
    await Test("configuration errors are isolated and corrupt state is an error", async () =>
    {
        var paths = new Paths("bad", root);
        File.WriteAllText(paths.ConfigurationFile, "{bad json");
        Check(await Run(paths) == 1);
        Check(StateStore.Status(paths).Status == "failed");
        File.WriteAllText(paths.StateFile, "{bad state");
        Check(await Cli.ExecuteAsync(["status", paths.Name], root) == 1);
    });
    await Test("direct runner, duplicate case-insensitive start, stop after config deletion", async () =>
    {
        var paths = Config("direct", ["wait"], "always");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Runner.RunAsync(paths, Guid.NewGuid(), false, r => { if (r.Success) ready.SetResult(); return Task.CompletedTask; }, default);
        active.Add(paths);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(await Cli.StartAsync(new Paths("DIRECT", root)) == "already running");
        File.Delete(paths.ConfigurationFile);
        Check(await Cli.StopAsync(paths) == "stopped");
        Check(await running == 0);
        Check(await Cli.StopAsync(paths) == "already stopped");
    });
    await Test("stop cancels retry delay and snapshot survives JSON edits", async () =>
    {
        var marker = Path.Combine(root, "retries.txt");
        var paths = Config("retry", ["count", marker, "1"], "on-failure", 0.4);
        active.Add(paths);
        var running = Run(paths);
        await Until(() => File.Exists(marker) && File.ReadAllLines(marker).Length >= 1);
        File.WriteAllText(paths.ConfigurationFile, "invalid replacement");
        await Until(() => File.ReadAllLines(marker).Length >= 2);
        Check(StateStore.Status(paths).Status == "running");
        await Cli.StopAsync(paths);
        Check(await running == 0);
        var count = File.ReadAllLines(marker).Length;
        await Task.Delay(600);
        Check(File.ReadAllLines(marker).Length == count);
    });
    await Test("state write failure terminates runner", async () =>
    {
        var paths = Config("state-error", ["wait"], "never");
        active.Add(paths);
        using var stop = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Runner.RunAsync(paths, Guid.NewGuid(), false, r => { ready.SetResult(); return Task.CompletedTask; }, stop.Token);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (var denyReplacement = new FileStream(paths.StateFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stop.Cancel();
            try { await running; throw new Exception("Expected state write failure."); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        Check(!StateStore.Active(paths));
    });
    await Test("always retries successful exits after cleaning ordinary descendants", async () =>
    {
        var marker = Path.Combine(root, "tree-attempts.txt");
        var paths = Config("tree-retry", ["tree-exit", marker], "always", 0.2);
        active.Add(paths);
        var running = Run(paths);
        await Until(() => File.Exists(marker) && File.ReadAllLines(marker).Length >= 2);
        var firstPid = int.Parse(File.ReadAllLines(marker)[0]);
        try { using var first = Process.GetProcessById(firstPid); Check(first.HasExited); }
        catch (ArgumentException) { }
        await Cli.StopAsync(paths);
        Check(await running == 0);
    });
    await Test("file rotation and retention", () =>
    {
        var paths = new Paths("rotation", root);
        using (var logging = new Logging(paths, false, size: 128, count: 3))
            for (var i = 0; i < 100; i++) logging.Logger.LogInformation("{Text:l}", new string('x', 100));
        var files = Directory.GetFiles(paths.LogDirectory, "*.log");
        Check(files.Length == 3);
        Check(files.Any(f => Path.GetFileNameWithoutExtension(f).Contains('_')));
        return Task.CompletedTask;
    });
    await Test("detachment succeeds or is explicitly rejected by the host", () =>
    {
        try
        {
            using var child = Native.CreateProcess(Environment.ProcessPath!, ["fixture", "wait"], detached: true);
            child.Kill();
        }
        catch (InvalidOperationException error) when (error.Message.Contains("prevents detaching"))
        {
            detachedSupported = false;
            Console.WriteLine("UNVERIFIED: detached lifetime and explicit breakaway; the host forbids leaving its Job. Remaining background tests use a contained native launcher injected into the test call only.");
        }
        return Task.CompletedTask;
    });
    await Test("background acknowledgement, independent IDs and runner-death cleanup", async () =>
    {
        var first = Config("background-one", ["tree", Path.Combine(root, "ordinary.pid")], "always");
        var second = Config("background-two", ["wait"], "never");
        active.AddRange([first, second]);
        Check(await Start(first) == "started");
        Check(await Start(second) == "started");
        await Until(() => File.Exists(Path.Combine(root, "ordinary.pid")));
        var descendant = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(root, "ordinary.pid"))));
        using var runner = Process.GetProcessById(StateStore.Read(first)!.Pid);
        runner.Kill();
        await runner.WaitForExitAsync();
        await descendant.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(StateStore.Status(first) is { Status: "stopped", ExitCode: null });
        Check(StateStore.Status(second).Status == "running");
        await Cli.StopAsync(second);
    });
    await Test("output reads can be cancelled while a writer remains open", async () =>
    {
        using var output = new OutputPipe();
        using var logging = new Logging(new Paths("drain", root), false);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await output.PumpAsync(logging.Logger, "stdout", cancel.Token).WaitAsync(TimeSpan.FromSeconds(2));
    });
    await Test("concurrent starts create exactly one daemon", async () =>
    {
        var marker = Path.Combine(root, "single.txt");
        var paths = Config("concurrent", ["mark-wait", marker], "never");
        active.Add(paths);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Start(paths)));
        Check(results.Count(x => x == "started") == 1);
        await Until(() => File.Exists(marker));
        Check(File.ReadAllLines(marker).Length == 1);
        await Cli.StopAsync(paths);
    });
    await Test("failed background startup reports configuration error and leaves no runner", async () =>
    {
        var paths = new Paths("bad-background", root);
        File.WriteAllText(paths.ConfigurationFile, "{}");
        try { await Start(paths); throw new Exception("Invalid configuration unexpectedly started."); }
        catch (InvalidOperationException error) { Check(error.Message.Contains("required", StringComparison.OrdinalIgnoreCase)); }
        Check(!StateStore.Active(paths));
        Check(StateStore.Read(paths)?.ExitCode == 1);
    });
    await Test("cancelled startup only stops its own runner", async () =>
    {
        var existing = Config("keep-running", ["wait"], "never");
        active.Add(existing);
        await Start(existing);
        var cancelled = Config("cancel-start", ["wait"], "always");
        active.Add(cancelled);
        using var cancellation = new CancellationTokenSource();
        try
        {
            await Cli.StartAsync(cancelled, cancellation.Token, (executable, arguments) =>
            {
                var child = Native.CreateProcess(executable, arguments);
                cancellation.Cancel();
                return child;
            });
            throw new Exception("Cancelled start unexpectedly succeeded.");
        }
        catch (OperationCanceledException) { }
        Check(!StateStore.Active(cancelled));
        Check(StateStore.Active(existing));
        await Cli.StopAsync(existing);
    });
    if (detachedSupported) await Test("breakaway survives stop and inherited output has bounded drain", async () =>
    {
        var marker = Path.Combine(root, "breakaway.pid");
        var paths = Config("breakaway", ["breakaway", marker], "never");
        active.Add(paths);
        Check(await Start(paths) == "started");
        await Until(() => File.Exists(marker));
        using var escaped = Process.GetProcessById(int.Parse(File.ReadAllText(marker)));
        try
        {
            var elapsed = Stopwatch.StartNew();
            await Cli.StopAsync(paths);
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(6), "Output drain took too long.");
            Check(!escaped.HasExited, "Breakaway process was killed.");
        }
        finally { if (!escaped.HasExited) { escaped.Kill(); await escaped.WaitForExitAsync(); } }
    });
    Console.WriteLine($"PASS: {passed} integration scenarios. Artifacts: {root}");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); Console.Error.WriteLine($"Artifacts: {root}"); return 1; }
finally
{
    foreach (var paths in active)
        try { if (StateStore.Active(paths)) await Cli.StopAsync(paths); } catch (Exception e) { Console.Error.WriteLine($"Cleanup {paths.Name}: {e.Message}"); }
}

Paths Config(string name, string[] arguments, string restart, double delay = 5, Dictionary<string, string>? environment = null)
{
    var paths = new Paths(name, root);
    File.WriteAllText(paths.ConfigurationFile, JsonSerializer.Serialize(new DaemonConfiguration
    {
        Executable = "ToolDock.Tests.exe", Arguments = ["fixture", .. arguments], WorkingDirectory = root,
        Restart = restart, RestartDelaySeconds = delay,
        Environment = (environment ?? []).ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value))
    }, Settings.Json));
    return paths;
}
Task<int> Run(Paths paths) => Runner.RunAsync(paths, Guid.NewGuid(), false, _ => Task.CompletedTask, default);
static async Task<(int Code, string Output)> List(string root)
{
    var original = Console.Out;
    using var output = new StringWriter();
    try
    {
        Console.SetOut(output);
        var code = await Cli.ExecuteAsync(["list"], root);
        return (code, output.ToString());
    }
    finally { Console.SetOut(original); }
}
Task<string> Start(Paths paths) => Cli.StartAsync(paths, launch: detachedSupported ? null :
    (executable, arguments) => Native.CreateProcess(executable, arguments));
string Logs(Paths paths) => string.Join('\n', Directory.GetFiles(paths.LogDirectory, "*.log").Select(File.ReadAllText));
async Task Test(string name, Func<Task> test) { await test().WaitAsync(TimeSpan.FromSeconds(45)); passed++; Console.WriteLine($"PASS {name}"); }
static void Check(bool condition, string? message = null) { if (!condition) throw new Exception(message ?? "Assertion failed."); }
static async Task Until(Func<bool> predicate)
{
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    while (!predicate()) await Task.Delay(20, limit.Token);
}

static async Task<int> Fixture(string[] arguments)
{
    switch (arguments[0])
    {
        case "environment-snapshot":
            File.AppendAllText(arguments[1], SecretTests.Hash(Environment.GetEnvironmentVariable("TD_LAYER"), Environment.GetEnvironmentVariable("TD_SECRET")) + "\n");
            Console.WriteLine("environment received");
            return 0;
        case "echo":
            File.WriteAllText(arguments[1], JsonSerializer.Serialize(new { args = arguments[2..], directory = Directory.GetCurrentDirectory(), environment = Environment.GetEnvironmentVariable("TOOLDOCK_TEST") }));
            Console.WriteLine("out {template}"); Console.Error.Write("err {template}"); return 0;
        case "exit": return int.Parse(arguments[1]);
        case "count": File.AppendAllText(arguments[1], "attempt\n"); return int.Parse(arguments[2]);
        case "mark-wait": File.AppendAllText(arguments[1], "attempt\n"); break;
        case "burst":
            await Task.WhenAll(Task.Run(() => { for (var i = 0; i < 1000; i++) Console.WriteLine($"output {i}"); }),
                Task.Run(() => { for (var i = 0; i < 1000; i++) Console.Error.WriteLine($"error {i}"); }));
            return 0;
        case "tree-exit":
            using (var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, ArgumentList = { "fixture", "wait" } })!)
                File.AppendAllText(arguments[1], child.Id + "\n");
            return 0;
        case "tree":
            using (var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, ArgumentList = { "fixture", "wait" } })!)
                File.WriteAllText(arguments[1], child.Id.ToString());
            break;
        case "breakaway":
            using (var child = Native.CreateProcess(Environment.ProcessPath!, ["fixture", "wait"],
                streams: [GetStdHandle(-10), GetStdHandle(-11), GetStdHandle(-12)], breakaway: true))
            {
                child.Resume(); File.WriteAllText(arguments[1], child.Id.ToString());
            }
            break;
    }
    await Task.Delay(Timeout.Infinite);
    return 0;
}
[DllImport("kernel32")] static extern nint GetStdHandle(int kind);
