using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ToolDock;
using Cli = ToolDock.Program;

internal static class SecretTests
{
    internal static async Task RunAsync(string testRoot, Func<string, Func<Task>, Task> test)
    {
        await test("secret CLI encrypts, replaces, lists names and deletes without exposing values", async () =>
        {
            var root = Path.Combine(testRoot, "secret-cli");
            var empty = await Command(root, ["secret", "list"]);
            Check(empty.Code == 0 && empty.Output == "" && !Directory.Exists(root));
            const string value = "test-only-token with spaces";
            var saved = await Command(root, ["secret", "set", "Example", "--stdin"], value + "\r\n");
            Check(saved.Code == 0 && !saved.Output.Contains(value) && !saved.Error.Contains(value));
            var store = new SecretStore(root);
            Check(store.Read()["example"] == value);
            var bytes = File.ReadAllBytes(Path.Combine(root, "secrets.dat"));
            Check(!Encoding.UTF8.GetString(bytes).Contains(value) && !Encoding.Unicode.GetString(bytes).Contains(value));
            var listed = await Command(root, ["secret", "list"]);
            Check(listed.Code == 0 && listed.Output.Trim() == "Example");
            Check((await Command(root, ["secret", "set", "EXAMPLE", "--stdin"], "replacement \n")).Code == 0);
            Check(store.Read()["example"] == "replacement ");
            Check((await Command(root, ["secret", "set", "second", "--stdin"], "line one\nline two\n")).Code == 0);
            Check(store.Read()["second"] == "line one\nline two");
            Check((await Command(root, ["secret", "delete", "example"])).Code == 0);
            Check(store.List().SequenceEqual(["second"]));
            Check((await Command(root, ["secret", "delete", "example"])).Code == 0);
            Check((await Command(root, ["secret", "set", "wrong", "value-in-arguments"])).Code == 2);
            Check((await Command(root, ["secret"])).Code == 2);
            Check((await Command(root, ["secret", "set", "invalid", "--stdin"], "test\0value")).Code == 1);
        });
        await test("global environment merges case-insensitively and does not change executable lookup", () =>
        {
            var root = Path.Combine(testRoot, "environment-merge");
            var paths = Configure(root, new { TD_LAYER = "daemon", td_secret = new { secret = "example" } });
            var inherited = Environment.GetEnvironmentVariable("TD_LAYER");
            try
            {
                Environment.SetEnvironmentVariable("TD_LAYER", "inherited");
                WriteGlobal(root, new { td_layer = "global", TD_GLOBAL = "global-only", PATH = "not-an-executable-search-path" });
                new SecretStore(root).Set("EXAMPLE", "test-only-secret");
                var snapshot = LaunchSnapshot.Load(paths);
                Check(snapshot.Environment["TD_LAYER"] == "daemon");
                Check(snapshot.Environment["TD_GLOBAL"] == "global-only");
                Check(snapshot.Environment["TD_SECRET"] == "test-only-secret");
                Check(snapshot.Environment["PATH"] == "not-an-executable-search-path");
                Check(snapshot.Executable.EndsWith("ToolDock.Tests.exe", StringComparison.OrdinalIgnoreCase));
                return Task.CompletedTask;
            }
            finally { Environment.SetEnvironmentVariable("TD_LAYER", inherited); }
        });
        await test("overridden secret references do not require the secret store", () =>
        {
            var root = Path.Combine(testRoot, "overridden-secret");
            var paths = Configure(root, new { TD_SECRET = "plain override" });
            WriteGlobal(root, new { td_secret = new { secret = "missing" } });
            File.WriteAllText(Path.Combine(root, "secrets.dat"), "corrupt store");
            Check(LaunchSnapshot.Load(paths).Environment["TD_SECRET"] == "plain override");
            return Task.CompletedTask;
        });
        await test("missing secrets and malformed environment fail without leaking values", async () =>
        {
            var root = Path.Combine(testRoot, "environment-errors");
            var paths = Configure(root, new { TD_SECRET = new { secret = "missing" } });
            var missing = await Command(root, ["run", paths.Name]);
            Check(missing.Code == 1 && missing.Error.Contains("missing"));
            Configure(root, new { TD_SECRET = "safe" });
            const string sentinel = "DO_NOT_PRINT_TEST_VALUE";
            File.WriteAllText(Path.Combine(root, "environment.json"), "{\"TD_SECRET\": \"" + sentinel);
            var invalid = await Command(root, ["run", paths.Name]);
            Check(invalid.Code == 1 && !invalid.Error.Contains(sentinel) && !invalid.Output.Contains(sentinel));
            File.WriteAllText(Path.Combine(root, "environment.json"), "{}");
            Configure(root, new { TD_SECRET = new { secret = "missing", value = sentinel } });
            var invalidReference = await Command(root, ["run", paths.Name]);
            Check(invalidReference.Code == 1 && !invalidReference.Error.Contains(sentinel));
        });
        await test("corrupt encrypted stores are not overwritten and do not affect control commands", async () =>
        {
            var root = Path.Combine(testRoot, "corrupt-secrets");
            var paths = Configure(root, new { TD_SECRET = new { secret = "example" } });
            new SecretStore(root).Set("example", "test-value");
            var file = Path.Combine(root, "secrets.dat");
            var bytes = File.ReadAllBytes(file);
            bytes[^1] ^= 0xff;
            File.WriteAllBytes(file, bytes);
            Check((await Command(root, ["secret", "list"])).Code == 1);
            Check((await Command(root, ["secret", "set", "other", "--stdin"], "new-value")).Code == 1);
            Check(File.ReadAllBytes(file).SequenceEqual(bytes));
            Check((await Command(root, ["run", paths.Name])).Code == 1);
            File.WriteAllText(Path.Combine(root, "environment.json"), "invalid");
            Check((await Command(root, ["list"])).Code == 0);
            Check((await Command(root, ["status", paths.Name, "--json"])).Code == 0);
            Check((await Command(root, ["stop", paths.Name])).Code == 0);
        });
        await test("secret mutations reject concurrent writers without losing stored data", () =>
        {
            var root = Path.Combine(testRoot, "secret-lock");
            var store = new SecretStore(root);
            store.Set("first", "first-value");
            using (var heldLock = new FileStream(Path.Combine(root, "secrets.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try { store.Set("second", "second-value"); throw new Exception("Concurrent write unexpectedly succeeded."); }
                catch (IOException) { }
                Check(store.Read()["first"] == "first-value");
            }
            store.Set("second", "second-value");
            Check(store.List().Length == 2);
            Check(!Directory.EnumerateFiles(root, "*.tmp").Any());
            return Task.CompletedTask;
        });
        await test("real child receives a fixed environment and secret snapshot until runner restart", async () =>
        {
            var root = Path.Combine(testRoot, "environment-snapshot");
            var marker = Path.Combine(root, "received.txt");
            var paths = Configure(root, new { TD_SECRET = new { secret = "example" } }, marker, "always");
            WriteGlobal(root, new { TD_LAYER = "old-global" });
            var store = new SecretStore(root);
            store.Set("example", "old-test-secret");
            using var stopping = new CancellationTokenSource();
            var running = Runner.RunAsync(paths, Guid.NewGuid(), false, _ => Task.CompletedTask, stopping.Token);
            try
            {
                await Until(() => File.Exists(marker) && FixtureFiles.ReadLines(marker).Length >= 1);
                WriteGlobal(root, new { TD_LAYER = "new-global" });
                store.Set("example", "new-test-secret");
                await Until(() => FixtureFiles.ReadLines(marker).Length >= 2);
            }
            finally { stopping.Cancel(); Check(await running == 0); }
            Check(FixtureFiles.ReadLines(marker).All(line => line == Hash("old-global", "old-test-secret")));
            Configure(root, new { TD_SECRET = new { secret = "example" } }, marker, "never");
            Check(await Runner.RunAsync(paths, Guid.NewGuid(), false, _ => Task.CompletedTask, default) == 0);
            Check(FixtureFiles.ReadLines(marker)[^1] == Hash("new-global", "new-test-secret"));
            foreach (var log in Directory.GetFiles(paths.LogDirectory, "*.log"))
            {
                var text = File.ReadAllText(log);
                Check(!text.Contains("old-test-secret") && !text.Contains("new-test-secret"));
            }
        });
    }

    private static Paths Configure(string root, object environment, string? marker = null, string restart = "never")
    {
        Directory.CreateDirectory(Path.Combine(root, "tools"));
        var paths = new Paths("worker", root);
        File.WriteAllText(paths.ConfigurationFile, JsonSerializer.Serialize(new
        {
            executable = "ToolDock.Tests.exe",
            arguments = marker is null ? new[] { "fixture", "exit", "0" } : ["fixture", "environment-snapshot", marker],
            workingDirectory = root, environment, restart, restartDelaySeconds = 0.2
        }));
        return paths;
    }
    private static void WriteGlobal(string root, object environment) =>
        File.WriteAllText(Path.Combine(root, "environment.json"), JsonSerializer.Serialize(environment));
    internal static string Hash(string? first, string? second) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(first + "\0" + second)));
    private static void Check(bool condition) { if (!condition) throw new Exception("Environment/secrets assertion failed."); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private static async Task<(int Code, string Output, string Error)> Command(string root, string[] arguments, string input = "")
    {
        var previousInput = Console.In;
        var previousOutput = Console.Out;
        var previousError = Console.Error;
        using var reader = new StringReader(input);
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetIn(reader); Console.SetOut(output); Console.SetError(error);
            var code = await Cli.ExecuteAsync(arguments, root);
            return (code, output.ToString(), error.ToString());
        }
        finally { Console.SetIn(previousInput); Console.SetOut(previousOutput); Console.SetError(previousError); }
    }
}
