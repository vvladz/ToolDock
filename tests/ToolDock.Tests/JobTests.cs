using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ToolDock;
using Cli = ToolDock.Program;

internal static class JobTests
{
    private const uint KillOnClose = 0x2000, Breakaway = 0x800, SilentBreakaway = 0x1000;

    internal static async Task RunAsync(string root, Func<string, Func<Task>, Task> test, Func<string, Func<Task>, Task> detachedTest)
    {
        await detachedTest("background runner and daemon survive closing the launcher's Job", () =>
            CheckLaunchAsync(root, "job-breakaway", KillOnClose | Breakaway, nested: false, expected: true));
        await detachedTest("background runner survives closing a silent-breakaway Job", () =>
            CheckLaunchAsync(root, "job-silent", KillOnClose | SilentBreakaway, nested: false, expected: true));
        await test("background start rejects a Job that forbids breakaway", () =>
            CheckLaunchAsync(root, "job-blocked", KillOnClose, nested: false, expected: false));
        await test("background start rejects a restrictive ancestor Job", () =>
            CheckLaunchAsync(root, "job-nested", KillOnClose, nested: true, expected: false));
    }

    private static async Task CheckLaunchAsync(string root, string name, uint flags, bool nested, bool expected)
    {
        var paths = new Paths(name, root);
        var marker = Path.Combine(root, name + ".result.json");
        var descendantMarker = Path.Combine(root, name + ".pid");
        File.WriteAllText(paths.ConfigurationFile, JsonSerializer.Serialize(new DaemonConfiguration
        {
            Executable = "ToolDock.Tests.exe", Arguments = ["fixture", "tree", descendantMarker],
            WorkingDirectory = root, Restart = "never"
        }, Settings.Json));
        using var job = CreateJob(flags);
        using var launcher = Native.CreateProcess(Environment.ProcessPath!,
            ["fixture", "job-start", root, name, marker, nested.ToString()], job: job);
        Process? runner = null;
        Process? descendant = null;
        try
        {
            launcher.Resume();
            await UntilAsync(() => File.Exists(marker));
            var result = JsonSerializer.Deserialize<StartupResult>(File.ReadAllText(marker))!;
            Check(result.Success == expected, result.Message);
            if (!expected)
            {
                Check(result.Message.Contains("detach", StringComparison.OrdinalIgnoreCase), result.Message);
                Check(!StateStore.Active(paths), "A rejected start left an active runner.");
                Check(!File.Exists(descendantMarker), "A rejected start launched the daemon.");
                return;
            }

            await UntilAsync(() => File.Exists(descendantMarker));
            runner = Process.GetProcessById(StateStore.Read(paths)!.Pid);
            descendant = Process.GetProcessById(int.Parse(File.ReadAllText(descendantMarker)));
            job.Dispose();
            await launcher.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(250);
            Check(!runner.HasExited && !descendant.HasExited && StateStore.Active(paths),
                "The runner or its daemon died with the launcher's Job.");
            Check(await Cli.StopAsync(paths) == "stopped", "The detached runner could not be stopped.");
            await runner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await descendant.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            try { if (StateStore.Active(paths)) await Cli.StopAsync(paths); }
            finally
            {
                if (!job.IsClosed) await Native.EmptyJobAsync(job);
                runner?.Dispose();
                descendant?.Dispose();
            }
        }
    }

    internal static async Task<int> StartAsync(string[] arguments)
    {
        using var inner = bool.Parse(arguments[3]) ? CreateJob(KillOnClose | Breakaway) : null;
        if (inner is not null) Native.Check(AssignProcessToJobObject(inner, new nint(-1)));
        StartupResult result;
        try { result = new(true, await Cli.StartAsync(new Paths(arguments[1], arguments[0]))); }
        catch (Exception error) { result = new(false, error.Message); }
        File.WriteAllText(arguments[2] + ".tmp", JsonSerializer.Serialize(result));
        File.Move(arguments[2] + ".tmp", arguments[2]);
        await Task.Delay(Timeout.Infinite);
        return 0;
    }

    private static KernelHandle CreateJob(uint flags)
    {
        var job = CreateJobObjectW(0, null);
        Native.Check(!job.IsInvalid);
        // JOBOBJECT_EXTENDED_LIMIT_INFORMATION is 144 bytes on the supported x64 target.
        var limits = new byte[144];
        BitConverter.GetBytes(flags).CopyTo(limits, 16);
        try { Native.Check(SetInformationJobObject(job, 9, limits, (uint)limits.Length)); return job; }
        catch { job.Dispose(); throw; }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern KernelHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetInformationJobObject(KernelHandle job, int kind, byte[] info, uint size);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(KernelHandle job, nint process);
}
