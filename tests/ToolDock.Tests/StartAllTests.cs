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
        await detachedTest("start --all isolates errors and repeated starts preserve running instances", async () =>
        {
            var root = Path.Combine(parent, "all-mixed");
            Directory.CreateDirectory(Path.Combine(root, "tools"));
            var good = new[] { new Paths("a-good", root), new Paths("z-good", root) };
            try
            {
                foreach (var paths in good)
                    File.WriteAllText(paths.ConfigurationFile, JsonSerializer.Serialize(new DaemonConfiguration
                    {
                        Executable = "ToolDock.Tests.exe", Arguments = ["fixture", "wait"], Restart = "never"
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
