using System.Text.Json;
using ToolDock;
using Cli = ToolDock.Program;

internal static class StartAllTests
{
    internal static async Task RunAsync(string parent, Func<string, Func<Task>, Task> test, Func<string, Func<Task>, Task> detachedTest)
    {
        await test("start --all distinguishes missing and empty configuration directories", async () =>
        {
            var root = Path.Combine(parent, "all-empty");
            Check(await Cli.ExecuteAsync(["start", "--all"], root) == 1);
            Check(!Directory.Exists(root));
            Directory.CreateDirectory(Path.Combine(root, "tools"));
            Check(await Cli.ExecuteAsync(["start", "--all"], root) == 0);
            Check(await Cli.ExecuteAsync(["start", "--all", "extra"], root) == 2);
        });
        await test("start --all skips other computers before creating any runner", async () =>
        {
            var root = Path.Combine(parent, "all-skipped");
            var tools = Directory.CreateDirectory(Path.Combine(root, "tools"));
            var config = new DaemonConfiguration { Executable = "missing-test-executable.exe", AutostartOn = [] };
            File.WriteAllText(Path.Combine(tools.FullName, "empty.json"), JsonSerializer.Serialize(config, Settings.Json));
            File.WriteAllText(Path.Combine(tools.FullName, "elsewhere.json"), JsonSerializer.Serialize(
                config with { AutostartOn = [Environment.MachineName + "-other"] }, Settings.Json));
            using var output = new StringWriter();
            var previous = Console.Out;
            try
            {
                Console.SetOut(output);
                Check(await Cli.ExecuteAsync(["start", "--all"], root) == 0);
            }
            finally { Console.SetOut(previous); }
            Check(output.ToString().Contains("empty: skipped") && output.ToString().Contains("elsewhere: skipped"));
            Check(output.ToString().Contains("0 succeeded, 2 skipped, 0 failed"));
            Check(!Directory.Exists(Path.Combine(root, "state")) && !Directory.Exists(Path.Combine(root, "logs")));
        });
        await test("invalid autostartOn values are configuration errors", async () =>
        {
            var root = Path.Combine(parent, "invalid-host-filter");
            var tools = Directory.CreateDirectory(Path.Combine(root, "tools"));
            foreach (var value in new[] { "null", "\"PC\"", "[null]", "[\" \"]" })
            {
                File.WriteAllText(Path.Combine(tools.FullName, "bad.json"),
                    $$"""{"executable":"ToolDock.Tests.exe","autostartOn":{{value}}}""");
                Check(await Cli.ExecuteAsync(["start", "--all"], root) == 1);
            }
            Check(!Directory.Exists(Path.Combine(root, "state")));
        });
        await detachedTest("named start and run allow a daemon excluded from autostart", async () =>
        {
            var root = Path.Combine(parent, "manual-start");
            Directory.CreateDirectory(Path.Combine(root, "tools"));
            var paths = new Paths("manual", root);
            var config = new DaemonConfiguration
            {
                Executable = "ToolDock.Tests.exe", Arguments = ["fixture", "exit", "0"], Restart = "never", AutostartOn = []
            };
            File.WriteAllText(paths.ConfigurationFile, JsonSerializer.Serialize(config, Settings.Json));
            Check(await Cli.ExecuteAsync(["run", paths.Name], root) == 0 && StateStore.Read(paths)?.ExitCode == 0);
            File.WriteAllText(paths.ConfigurationFile, JsonSerializer.Serialize(config with { Arguments = ["fixture", "wait"] }, Settings.Json));
            try
            {
                Check(await Cli.ExecuteAsync(["start", paths.Name], root) == 0 && StateStore.Active(paths));
                var runId = StateStore.Read(paths)!.RunId;
                Check(await Cli.ExecuteAsync(["start", "--all"], root) == 0 && StateStore.Read(paths)!.RunId == runId);
            }
            finally { if (StateStore.Active(paths)) await Cli.StopAsync(paths); }
        });
        await detachedTest("start --all isolates errors and repeated starts preserve running instances", async () =>
        {
            var root = Path.Combine(parent, "all-mixed");
            Directory.CreateDirectory(Path.Combine(root, "tools"));
            var good = new[] { new Paths("a-good", root), new Paths("z-good", root) };
            try
            {
                var config = new DaemonConfiguration
                {
                    Executable = "ToolDock.Tests.exe", Arguments = ["fixture", "wait"], Restart = "never"
                };
                File.WriteAllText(good[0].ConfigurationFile, JsonSerializer.Serialize(config, Settings.Json));
                File.WriteAllText(good[1].ConfigurationFile, JsonSerializer.Serialize(config with
                {
                    AutostartOn = [Environment.MachineName + "-other", Environment.MachineName.ToLowerInvariant()]
                }, Settings.Json));
                var badJson = new Paths("b-bad-json", root);
                var badExe = new Paths("c-bad-exe", root);
                File.WriteAllText(badJson.ConfigurationFile, "{broken");
                File.WriteAllText(badExe.ConfigurationFile, "{\"executable\":\"missing-test-executable.exe\"}");
                Check(await Cli.ExecuteAsync(["start", "--all"], root) == 1);
                Check(good.All(StateStore.Active));
                var ids = good.Select(paths => StateStore.Read(paths)!.RunId).ToArray();
                File.Delete(badJson.ConfigurationFile);
                File.Delete(badExe.ConfigurationFile);
                Check(await Cli.ExecuteAsync(["start", "--all"], root) == 0);
                Check(good.Select(paths => StateStore.Read(paths)!.RunId).SequenceEqual(ids));
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                Check(await Cli.ExecuteAsync(["start", "--all"], root, cancelled.Token) == 1);
                Check(good.All(StateStore.Active));
            }
            finally
            {
                foreach (var paths in good)
                    if (StateStore.Active(paths)) await Cli.StopAsync(paths);
            }
        });
    }

    private static void Check(bool value) { if (!value) throw new Exception("start --all assertion failed."); }
}
