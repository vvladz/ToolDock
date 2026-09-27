using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ToolDock.Common;
using ToolDock.Starter.Processes;
using ToolDock.Starter.Supervisor;
using ToolDock.Updating;

namespace ToolDock.IntegrationTests;

internal static partial class Suite
{
    private static async Task RetentionAsync()
    {
        using var f = new Fixture();
        Require(f.Run().Status == UpdateStatus.Completed, "Initial install failed.");
        var oldest = (await f.Snapshot()).PackageRoot(f.Paths, "app");
        using (var lease = VersionLease.Acquire(oldest))
        {
            for (var i = 2; i <= 5; i++)
            {
                f.Source.Version = $"v{i}";
                Require(f.Run().Status == UpdateStatus.Completed, "Retention changed a successful update outcome.");
            }
            Require(Directory.Exists(oldest), "Running version was pruned.");
        }
        Require(f.Run().Status == UpdateStatus.Completed && !Directory.Exists(oldest), $"Unused version was not pruned later. {f.Log}");
        var packageRoot = Path.Combine(f.Paths.Tools, "app");
        var versions = Directory.GetDirectories(packageRoot).Where(p => File.Exists(Path.Combine(p, ReleaseInstaller.MarkerName)) &&
            !File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)).ToArray();
        Require(versions.Length == 3, "Retention did not keep the default three versions.");
        var staging = Path.Combine(packageRoot, ".install-keep");
        Directory.CreateDirectory(staging);
        var outside = Path.Combine(f.Paths.Root, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "keep.txt"), "keep");
        await Junction.SwitchAsync(Path.Combine(packageRoot, "v-outside"), outside, default);
        // An interrupted deletion uses a private directory; a locked file defers cleanup safely.
        var retired = Path.Combine(packageRoot, ".cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(retired);
        await JsonFiles.WriteAtomicAsync(Path.Combine(retired, ReleaseInstaller.MarkerName), new VersionMarker("old", "example/app", "app.zip", DateTimeOffset.UtcNow));
        var locked = Path.Combine(retired, "locked");
        await File.WriteAllTextAsync(locked, "locked");
        using (var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            Require(f.Run().Status == UpdateStatus.Completed && Directory.Exists(retired), "Cleanup failure changed update status.");
        f.Run();
        Require(!Directory.Exists(retired), "Interrupted cleanup not retried.");
        Require(Directory.Exists(staging) && File.Exists(Path.Combine(outside, "keep.txt")), "Cleanup traversed staging or a junction.");

        using var running = new Fixture();
        running.Run();
        var usedRoot = (await running.Snapshot()).PackageRoot(running.Paths, "app");
        foreach (var source in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(source, Path.Combine(usedRoot, Path.GetFileName(source)), true);
        using var child = Fixture.Start(Path.Combine(usedRoot, Path.GetFileName(Executable)), ["--sleep"]);
        try
        {
            for (var i = 2; i <= 5; i++) { running.Source.Version = $"v{i}"; running.Run(); }
            Require(Directory.Exists(usedRoot) && !child.HasExited, "Cleanup removed a version used by an orphaned command.");
        }
        finally { if (!child.HasExited) child.Kill(); await child.WaitForExitAsync(); }
        running.Run();
        Require(!Directory.Exists(usedRoot), "Exited command's version was not pruned.");
    }

    private static async Task ConfigureRuntimeAsync(Fixture f, string mode = "", bool notify = true)
    {
        var root = Path.Combine(f.Paths.Tools, "runtime", "v1");
        Directory.CreateDirectory(root);
        foreach (var source in Directory.GetFiles(AppContext.BaseDirectory))
            File.Copy(source, Path.Combine(root, Path.GetFileName(source)), true);
        var definition = new ToolDefinition
        {
            Repo = "example/runtime", Asset = "runtime.zip",
            Commands = new()
            {
                ["notify"] = new CommandDefinition
                {
                    Executable = Path.GetFileName(Executable),
                    Environment = new()
                    {
                        ["NOTIFY_RECORD"] = EnvironmentValue.FromLiteral(Path.Combine(f.Paths.Root, "notification.json")),
                        ["NOTIFY_VALUE"] = EnvironmentValue.FromVariable("message.value"),
                        ["NOTIFY_MODE"] = EnvironmentValue.FromLiteral(mode)
                    }
                },
                ["runtime"] = new CommandDefinition { Executable = Path.GetFileName(Executable) }
            },
            Daemons = new()
            {
                ["runtime"] = new DaemonDefinition { Executable = Path.GetFileName(Executable), Arguments = ["--sleep"], RestartOnUpdate = true, Autostart = true }
            }
        };
        var catalog = new ToolCatalog { Variables = new() { ["message.value"] = "configured value" }, Tools = new() { ["runtime"] = definition } };
        await JsonFiles.WriteAtomicAsync(f.Paths.InstalledStateFile, new InstalledState
        {
            ActiveCatalog = catalog, Tools = new()
            {
                ["runtime"] = new InstalledTool { Version = "v1", Root = Path.GetRelativePath(f.Paths.Root, root), ActivationId = "old" }
            }
        });
        await JsonFiles.WriteAtomicAsync(f.Paths.ConfigFile, new ToolDockConfig
        {
            CatalogUrl = "https://127.0.0.1:1/catalog", NotificationCommand = notify ? "notify" : null, NotificationTimeoutSeconds = 1
        });
    }

    private static async Task NotificationsAsync()
    {
        using var f = new Fixture();
        await ConfigureRuntimeAsync(f, notify: false);
        var sender = new NotificationSender(f.Paths, f.Logs.CreateLogger("notification"));
        var record = Path.Combine(f.Paths.Root, "notification.json");
        await sender.SendAsync("tool-update-success", "disabled");
        Require(!File.Exists(record), "Disabled notification ran a process.");
        await ConfigureRuntimeAsync(f);
        const string text = "First line\nВторая строка\r\nLast line without newline";
        await sender.SendAsync("tool-update-success", text);
        using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(record)))
        {
            Require(json.RootElement.GetProperty("Type").GetString() == "tool-update-success" &&
                json.RootElement.GetProperty("Text").GetString() == text &&
                json.RootElement.GetProperty("Value").GetString() == "configured value", "Notification command contract changed.");
        }
        await ConfigureRuntimeAsync(f, "fail");
        await sender.SendAsync("tool-update-failure", "failed handler");
        Require(f.Log.Contains("exited with code 17"), "Handler failure not logged.");
        await ConfigureRuntimeAsync(f, "sleep");
        var clock = Stopwatch.StartNew();
        await sender.SendAsync("daemon-crash", "timed out handler");
        Require(clock.Elapsed < TimeSpan.FromSeconds(8) && f.Log.Contains("Notification delivery failed"), "Handler timeout did not bound delivery.");
        using var recorded = JsonDocument.Parse(await File.ReadAllTextAsync(record));
        var pid = recorded.RootElement.GetProperty("Pid").GetInt32();
        Require(!Process.GetProcesses().Any(p => { using (p) return p.Id == pid; }), "Timed-out handler is still running.");
        await ConfigureRuntimeAsync(f, "no-input");
        clock.Restart();
        await sender.SendAsync("daemon-crash", new string('x', 100000));
        Require(clock.Elapsed < TimeSpan.FromSeconds(8), "Handler that ignores stdin blocked notification delivery.");
        await JsonFiles.WriteAtomicAsync(f.Paths.ConfigFile, new ToolDockConfig { CatalogUrl = "https://test.local", NotificationCommand = "missing" });
        await sender.SendAsync("daemon-crash", "missing handler");
        Require(f.Log.Contains("Unknown command: missing"), "Missing handler was not recorded locally.");
    }

    private static async Task DaemonsAsync()
    {
        using var f = new Fixture();
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(p => (string)p.Key, p => (string)p.Value!);
        foreach (var error in new[] { "last stderr line", "none" })
        {
            var events = new List<DaemonExit>();
            var path = Path.Combine(f.Paths.Logs, error == "none" ? "quiet.log" : "crash.log");
            await using (var process = ManagedToolProcess.Start("crash", Executable, ["--exit", error], environment, path,
                             onUnexpectedExit: events.Add))
            {
                await process.ExitObserved.WaitAsync(TimeSpan.FromSeconds(10));
                Require(events.Count == 1 && events[0].ExitCode == 0 &&
                    events[0].LastError == (error == "none" ? null : error), "Unexpected exit event missing or incorrect.");
                Require(File.ReadAllText(path).Contains($"process exited pid={process.ProcessId} code=0"), "Spontaneous exit not logged promptly.");
                await process.StopAsync();
            }
            Require(events.Count == 1 && File.ReadAllText(path).Split("process exited pid=").Length == 2, "Exit duplicated during cleanup.");
        }
        var stopEvents = new List<DaemonExit>();
        await using (var process = ManagedToolProcess.Start("stopped", Executable, ["--sleep"], environment,
                         Path.Combine(f.Paths.Logs, "stopped.log"), onUnexpectedExit: stopEvents.Add))
        {
            await process.StopAsync();
            Require(stopEvents.Count == 0, "Requested stop emitted crash.");
        }
        await ConfigureRuntimeAsync(f);
        await using var supervisor = new ToolSupervisor(f.Paths, f.Logs.CreateLogger<ToolSupervisor>(), f.Events);
        Require((await supervisor.ExecuteAsync("start", "runtime", default)).StartsWith("OK running"), "Daemon did not start.");
        var snapshot = await f.Snapshot();
        var id = Guid.NewGuid().ToString("N");
        var old = snapshot.State.Tools["runtime"];
        snapshot.State.Tools["runtime"] = new InstalledTool { Version = old.Version, Root = old.Root, ActivationId = id };
        await JsonFiles.WriteAtomicAsync(f.Paths.InstalledStateFile, snapshot.State);
        Require((await supervisor.PackageUpdatedAsync("runtime", false, default, id)).StartsWith("OK restarted"), "Update restart failed.");
        var status = await supervisor.ExecuteAsync("status", "runtime", default);
        Require(await supervisor.PackageUpdatedAsync("runtime", false, default, id) == "OK no daemon changes", "Lost-reply retry repeated restart.");
        Require(await supervisor.ExecuteAsync("status", "runtime", default) == status, "Retry changed process instance.");
        await supervisor.ExecuteAsync("restart", "runtime", default);
        await supervisor.ExecuteAsync("stop", "runtime", default);
        Require(f.Events.Messages.Count == 0, "Intentional lifecycle command emitted crash.");
    }

    private static async Task DurableReconciliationAsync()
    {
        using var f = new Fixture();
        await ConfigureRuntimeAsync(f);
        var id = Guid.NewGuid().ToString("N");
        await using (var supervisor = new ToolSupervisor(f.Paths, f.Logs.CreateLogger<ToolSupervisor>(), f.Events))
        {
            await supervisor.ExecuteAsync("start", "runtime", default);
            var snapshot = await f.Snapshot();
            var installed = snapshot.State.Tools["runtime"];
            snapshot.State.Tools["runtime"] = new InstalledTool { Version = installed.Version, Root = installed.Root, ActivationId = id };
            snapshot.Catalog.Tools["runtime"].Daemons["runtime"] = new DaemonDefinition { Executable = "missing.exe", RestartOnUpdate = true };
            await JsonFiles.WriteAtomicAsync(f.Paths.InstalledStateFile, snapshot.State);
            Require((await supervisor.PackageUpdatedAsync("runtime", false, default, id)).StartsWith("ERROR"), "Expected restart failure did not occur.");
        }
        var repaired = await f.Snapshot();
        repaired.Catalog.Tools["runtime"].Daemons["runtime"] = new DaemonDefinition
        { Executable = Path.GetFileName(Executable), Arguments = ["--sleep"], RestartOnUpdate = true };
        await JsonFiles.WriteAtomicAsync(f.Paths.InstalledStateFile, repaired.State);
        await using (var restarted = new ToolSupervisor(f.Paths, f.Logs.CreateLogger<ToolSupervisor>(), f.Events))
        {
            Require((await restarted.PackageUpdatedAsync("runtime", false, default, id)).StartsWith("OK restarted"),
                "New Starter lost an unfinished restart.");
            await restarted.ExecuteAsync("stop", "runtime", default);
        }
        await using (var acknowledged = new ToolSupervisor(f.Paths, f.Logs.CreateLogger<ToolSupervisor>(), f.Events))
        {
            Require(await acknowledged.PackageUpdatedAsync("runtime", false, default, id) == "OK no daemon changes", "Persisted receipt was not honored.");
            repaired.Catalog.Tools["runtime"].Daemons["runtime"] = new DaemonDefinition
            { Executable = Path.GetFileName(Executable), Arguments = ["--exit", "supervisor stderr"] };
            await JsonFiles.WriteAtomicAsync(f.Paths.InstalledStateFile, repaired.State);
            await acknowledged.ExecuteAsync("start", "runtime", default);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (f.Events.Messages) if (f.Events.Messages.Count > 0) break;
                await Task.Delay(20);
            }
            await acknowledged.ExecuteAsync("stop", "runtime", default);
        }
        Require(f.Events.Messages.Count == 1 && f.Events.Messages[0].Type == "daemon-crash" &&
            f.Events.Messages[0].Text.Contains("supervisor stderr"), "Supervisor did not deliver exactly one crash event.");
    }

    private static async Task ClientsAsync()
    {
        using var f = new Fixture();
        await ConfigureRuntimeAsync(f, notify: false);
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        foreach (var project in new[] { "Client", "Updater", "Starter" })
        {
            var output = Path.Combine(repository, "src", "ToolDock." + project, "bin", configuration, "net10.0-windows");
            foreach (var source in Directory.GetFiles(output)) File.Copy(source, Path.Combine(f.Paths.Bin, Path.GetFileName(source)), true);
        }
        var client = Path.Combine(f.Paths.Bin, "ToolDock.Client.exe");
        using (var help = Fixture.Start(client, ["exec", "runtime", "--", "--echo-arguments", "--help", "two words"]))
        {
            var output = await help.StandardOutput.ReadToEndAsync();
            await help.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(help.ExitCode == 0 && JsonSerializer.Deserialize<string[]>(output)!.SequenceEqual(new[] { "--help", "two words" }),
                "Client help handling intercepted or changed child arguments.");
        }
        using (var process = Fixture.Start(client, ["variable", "set", "root.proof", "custom"]))
        {
            await process.WaitForExitAsync();
            Require(process.ExitCode == 0 && File.Exists(f.Paths.VariablesFile), "Fresh client did not infer custom installation root.");
        }
        var commands = Enumerable.Range(0, 2).Select(_ => Fixture.Start(client, ["exec", "runtime", "--", "--hold"])).ToArray();
        try
        {
            var output = commands.Select(p => p.StandardOutput.ReadToEndAsync()).ToArray();
            var errors = commands.Select(p => p.StandardError.ReadToEndAsync()).ToArray();
            await Task.WhenAll(commands.Select(p => p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10))));
            Require(commands.All(p => p.ExitCode == 23), "Overlapping command failed to launch or lost exit code.");
            Require((await Task.WhenAll(output)).All(t => t.Contains("child stdout")) &&
                (await Task.WhenAll(errors)).All(t => t.Contains("child stderr")), "Child console streams not preserved.");
        }
        finally { foreach (var p in commands) p.Dispose(); }
        using (var updater = Fixture.Start(Path.Combine(f.Paths.Bin, "ToolDock.Updater.exe"), []))
        {
            await updater.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Require(updater.ExitCode == 1 && f.Log.Contains("Fetching catalog"), "Fresh updater did not infer custom root.");
        }
        // Use an empty catalog to avoid autostart during the executable/root-selection test.
        await JsonFiles.WriteAtomicAsync(f.Paths.InstalledStateFile, new InstalledState { ActiveCatalog = new ToolCatalog() });
        using var starter = Fixture.Start(Path.Combine(f.Paths.Bin, "ToolDock.Starter.exe"), []);
        try
        {
            var response = await new StarterClient(f.Paths).SendAsync("list", TimeSpan.FromSeconds(10));
            Require(response == "OK no daemons" && File.Exists(Path.Combine(f.Paths.Logs, "ToolDock.Starter.log")), "Fresh starter did not infer custom root.");
        }
        finally
        {
            if (!starter.HasExited) starter.Kill();
            await starter.WaitForExitAsync();
        }
    }
}
