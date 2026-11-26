using System.Diagnostics;
using System.Text.Json;
using ToolDock;
using Cli = ToolDock.Program;

internal static class CmdTests
{
    internal static async Task RunAsync(string parent, Func<string, Func<Task>, Task> test)
    {
        var root = Path.Combine(parent, "cmd space & (test)");
        var bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(Path.Combine(root, "tools"));
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", bin + ";" + originalPath);
        var active = new List<Paths>();
        try
        {
            foreach (var version in new[] { "one", "two" })
            {
                var directory = Path.Combine(root, "versions", version);
                Directory.CreateDirectory(directory);
                foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
                    File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            }
            File.WriteAllText(Path.Combine(root, "installed"), "one\n");
            File.WriteAllText(Path.Combine(bin, "fixture launcher.cmd"), """
                @echo off
                setlocal DisableDelayedExpansion
                set "_toolbox_dir=%~dp0.."
                set "_toolbox_version="
                if not exist "%_toolbox_dir%\installed" exit /b 1
                set /p "_toolbox_version="<"%_toolbox_dir%\installed"
                if not defined _toolbox_version exit /b 1
                "%_toolbox_dir%\versions\%_toolbox_version%\ToolDock.Tests.exe" %*
                exit /b %errorlevel%
                """.ReplaceLineEndings("\r\n"));

            await test("CMD forwards literal arguments, environment, directory and both streams", async () =>
            {
                var output = Path.Combine(root, "arguments.json");
                string[] values = ["", "two words", "a\"b", "\"& echo UNEXPECTED &\"", "&|<>()^", "%PATH%", "%UNDEFINED%", "100%", "!PATH!", "кирилиця", @"C:\two words\", "ends\\\"", "a\"\"b"];
                var paths = Config("arguments", ["echo", output, .. values]);
                Check(await Run(paths) == 0);
                using var json = JsonDocument.Parse(File.ReadAllText(output));
                var actual = json.RootElement.GetProperty("args").EnumerateArray().Select(x => x.GetString()).ToArray();
                Check(actual.SequenceEqual(values), JsonSerializer.Serialize(actual));
                Check(json.RootElement.GetProperty("directory").GetString() == root);
                Check(json.RootElement.GetProperty("environment").GetString() == "cmd-value");
                var logs = Logs(paths);
                Check(logs.Contains("[stdout] out {template}") && logs.Contains("[stderr] err {template}"));
                Check(!logs.Contains("[stdout] UNEXPECTED"));
            });
            await test("CMD propagates the program exit code", async () =>
            {
                var paths = Config("exit", ["exit", "7"]);
                Check(await Run(paths) == 1);
                Check(StateStore.Status(paths).ExitCode == 1);
                Check(Logs(paths).Contains("daemon exited code=7"));
            });
            await test("CMD retry selects the installed version without reloading daemon JSON", async () =>
            {
                var marker = Path.Combine(root, "versions.txt");
                var paths = Config("versions", ["version-count", marker], "on-failure");
                active.Add(paths);
                Check(await Cli.StartAsync(paths) == "started");
                await Until(() => File.Exists(marker) && FixtureFiles.ReadText(marker).Contains("one"));
                File.WriteAllText(paths.ConfigurationFile, "invalid replacement");
                File.WriteAllText(Path.Combine(root, "installed"), "two\n");
                await Until(() => FixtureFiles.ReadText(marker).Contains("two"));
                Check(await Cli.StopAsync(paths) == "stopped");
            });
            await test("CMD stop terminates the wrapper and ordinary descendants", async () =>
            {
                var marker = Path.Combine(root, "descendant.pid");
                var paths = Config("tree", ["tree", marker]);
                active.Add(paths);
                Check(await Cli.StartAsync(paths) == "started");
                await Until(() => File.Exists(marker));
                using var descendant = Process.GetProcessById(int.Parse(FixtureFiles.ReadText(marker)));
                Check(await Cli.StopAsync(paths) == "stopped");
                await descendant.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check(!StateStore.Active(paths));
            });
            await test("CMD rejects unsupported command text before creating a process", () =>
            {
                foreach (var value in new[] { "line\nbreak", "line\rbreak", new string('x', 8192) })
                    Reject(() => CmdCommand.Build(@"C:\tool.cmd", [value]));
                foreach (var path in new[] { @"C:\%PATH%\tool.cmd", @"C:\caret^\tool.cmd" })
                    Reject(() => CmdCommand.Build(path, []));
                return Task.CompletedTask;
            });
        }
        finally
        {
            foreach (var paths in active)
                if (StateStore.Active(paths)) await Cli.StopAsync(paths);
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }

        Paths Config(string name, string[] arguments, string restart = "never")
        {
            var paths = new Paths(name, root);
            File.WriteAllText(paths.ConfigurationFile, JsonSerializer.Serialize(new DaemonConfiguration
            {
                Executable = "fixture launcher.cmd", Arguments = ["fixture", .. arguments], WorkingDirectory = root,
                Restart = restart, RestartDelaySeconds = 0.2,
                Environment = new() { ["TOOLDOCK_TEST"] = JsonSerializer.SerializeToElement("cmd-value") }
            }, Settings.Json));
            return paths;
        }
    }

    private static Task<int> Run(Paths paths) => Runner.RunAsync(paths, Guid.NewGuid(), false, _ => Task.CompletedTask, default);
    private static string Logs(Paths paths) => string.Join('\n', Directory.GetFiles(paths.LogDirectory).Select(File.ReadAllText));
    private static void Check(bool value, string? message = null) { if (!value) throw new Exception(message ?? "CMD assertion failed."); }
    private static void Reject(Action action)
    {
        try { action(); throw new Exception("Expected invalid CMD input to be rejected."); }
        catch (InvalidDataException) { }
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }
}
