using System.Collections;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ToolDock;

internal static class Settings
{
    internal static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);
    internal const long LogSize = 20 * 1024 * 1024;
    internal const int LogCount = 10;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
}

internal sealed class Paths
{
    internal string Root { get; }
    internal string Name { get; }
    internal string StateFile => Path.Combine(Root, "state", Name + ".json");
    internal string LockFile => Path.Combine(Root, "state", Name + ".lock");
    internal string LogDirectory => Path.Combine(Root, "logs", Name);
    internal string ConfigurationFile => Path.Combine(Root, "tools", Name + ".json");
    internal Paths(string name, string? root = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("Daemon must be a file name without a path or .json extension.");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])))
            throw new ArgumentException("Reserved Windows device name.");
        Name = name.ToLowerInvariant();
        Root = ResolveRoot(root);
    }
    internal static string ResolveRoot(string? root) =>
        Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "ToolDock"));

    internal static string[] ListDaemons(string? root = null, bool requireDirectory = false)
    {
        try
        {
            return Directory.EnumerateFiles(Path.Combine(ResolveRoot(root), "tools"), "*", SearchOption.TopDirectoryOnly)
                .Where(file => string.Equals(Path.GetExtension(file), ".json", StringComparison.OrdinalIgnoreCase))
                .Select(file => Path.GetFileNameWithoutExtension(file))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (DirectoryNotFoundException) when (!requireDirectory) { return []; }
    }

    internal string EventName(Guid runId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToUpperInvariant() + "\0" + Name.ToUpperInvariant())));
        return $"Global\\ToolDock.{WindowsIdentity.GetCurrent().User!.Value}.{hash}.{runId:N}";
    }
}

internal sealed record DaemonConfiguration
{
    public required string Executable { get; init; }
    public string[] Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public Dictionary<string, JsonElement> Environment { get; init; } = [];
    public string Restart { get; init; } = "on-failure";
    public double RestartDelaySeconds { get; init; } = 5;
}

internal sealed record LaunchSnapshot(string Executable, string[] Arguments, string Directory,
    IReadOnlyDictionary<string, string> Environment, string Restart, TimeSpan Delay)
{
    internal static LaunchSnapshot Load(Paths paths)
    {
        DaemonConfiguration config;
        try
        {
            config = JsonSerializer.Deserialize<DaemonConfiguration>(File.ReadAllText(paths.ConfigurationFile), Settings.Json)
                ?? throw new InvalidDataException("Configuration must be an object.");
        }
        catch (JsonException) { throw new InvalidDataException("Invalid daemon configuration JSON or missing required properties."); }
        if (string.IsNullOrWhiteSpace(config.Executable) || config.Executable.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !(config.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
              config.Executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("executable must be an .exe or .cmd file name on PATH.");
        if (config.Arguments is null || config.Arguments.Any(x => x is null || x.Contains('\0')))
            throw new InvalidDataException("arguments must be an array of strings without NUL characters.");
        if (config.Restart is not ("never" or "on-failure" or "always") || !double.IsFinite(config.RestartDelaySeconds) ||
            config.RestartDelaySeconds <= 0 || config.RestartDelaySeconds > int.MaxValue / 1000d)
            throw new InvalidDataException("Invalid restart policy or positive restartDelaySeconds.");
        var directory = Path.GetFullPath(config.WorkingDirectory ?? ".", Path.GetDirectoryName(paths.ConfigurationFile)!);
        if (!System.IO.Directory.Exists(directory)) throw new DirectoryNotFoundException($"Working directory does not exist: {directory}");
        if (config.Executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) && directory.StartsWith(@"\\"))
            throw new InvalidDataException("CMD launchers require a local workingDirectory.");
        var executable = (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')
            .Select(p => p.Trim().Trim('"')).Where(p => p.Length > 0)
            .Select(p => Path.Combine(Path.GetFullPath(p), config.Executable)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"Executable not found on PATH: {config.Executable}");
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry item in System.Environment.GetEnvironmentVariables()) environment[(string)item.Key] = (string)item.Value!;
        EnvironmentSettings.Apply(paths.Root, config.Environment, environment);
        return new(Native.FinalPath(executable), config.Arguments, directory, environment, config.Restart, TimeSpan.FromSeconds(config.RestartDelaySeconds));
    }
}

internal sealed record RunnerState(int Pid, DateTimeOffset StartedAtUtc, DateTimeOffset? FinishedAtUtc, int? ExitCode, Guid RunId);
internal sealed record StatusResult(string Daemon, string Status, int? Pid, DateTimeOffset? StartedAtUtc, DateTimeOffset? FinishedAtUtc, int? ExitCode);

internal static class StateStore
{
    internal static FileStream? TryLock(Paths paths)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LockFile)!);
        var file = new FileStream(paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        try { file.Lock(0, 1); if (file.Length == 0) file.SetLength(1); return file; }
        catch (IOException e) when ((e.HResult & 0xffff) == 33) { file.Dispose(); return null; }
        catch { file.Dispose(); throw; }
    }
    internal static bool Active(Paths paths)
    {
        // Probe the exclusively locked byte by reading it. Short commands must not
        // acquire the runner lock, or concurrent starts can mistake a probe for a runner.
        try
        {
            using var probe = new FileStream(paths.LockFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _ = probe.ReadByte();
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException e) when ((e.HResult & 0xffff) == 33) { return true; }
    }
    internal static RunnerState? Read(Paths paths)
    {
        string text;
        try
        {
            using var file = new FileStream(paths.StateFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            text = reader.ReadToEnd();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        var state = JsonSerializer.Deserialize<RunnerState>(text, Settings.Json);
        if (state is null || state.Pid <= 0 || state.StartedAtUtc == default || state.RunId == Guid.Empty ||
            (state.ExitCode is null) != (state.FinishedAtUtc is null))
            throw new InvalidDataException("Invalid runner state.");
        return state;
    }
    internal static void Write(Paths paths, RunnerState state)
    {
        var temporary = paths.StateFile + "." + state.RunId.ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, Settings.Json), new UTF8Encoding(false));
            File.Move(temporary, paths.StateFile, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static StatusResult Status(Paths paths)
    {
        var active = Active(paths);
        var state = Read(paths);
        return new(paths.Name, active ? "running" : state?.ExitCode is > 0 or < 0 ? "failed" : "stopped",
            state?.Pid, state?.StartedAtUtc, state?.FinishedAtUtc, active ? null : state?.ExitCode);
    }
}
