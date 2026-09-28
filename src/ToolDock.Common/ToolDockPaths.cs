using System.Security.Cryptography;
using System.Text;

namespace ToolDock.Common;

public sealed class ToolDockPaths
{
    public ToolDockPaths(string? root = null)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root ?? Environment.GetEnvironmentVariable("TOOLDOCK_HOME") ??
            InstalledRoot() ?? DefaultRoot));
    }

    public string Root { get; }
    public string Bin => Path.Combine(Root, "bin");
    public string Tools => Path.Combine(Root, "tools");
    public string State => Path.Combine(Root, "state");
    public string Logs => Path.Combine(Root, "logs");
    public string Temp => Path.Combine(Root, "temp");
    public string ConfigFile => Path.Combine(Root, "config.json");
    public string VariablesFile => Path.Combine(Root, "variables.json");
    public string Secrets => Path.Combine(Root, "secrets");
    public string SecretsFile => Path.Combine(Secrets, "secrets.dat");
    public string InstalledStateFile => Path.Combine(State, "installed.json");
    public string CatalogCacheFile => Path.Combine(State, "catalog.json");
    public string UpdateFailuresFile => Path.Combine(State, "update-failures.json");
    public string StarterPipeName => "ToolDock.Starter.v1" + InstanceSuffix;
    public string StarterMutexName => @"Local\ToolDock.Starter" + InstanceSuffix;
    public string UpdateMutexName => @"Local\ToolDock.Update" + InstanceSuffix;

    private static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tooldock");
    private string InstanceSuffix => "." + PathIdentity(Root);

    public static string PathIdentity(string path) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant())))[..24];

    private static string? InstalledRoot()
    {
        var bin = new DirectoryInfo(AppContext.BaseDirectory);
        return string.Equals(bin.Name, "bin", StringComparison.OrdinalIgnoreCase) &&
            bin.Parent is { } parent && File.Exists(Path.Combine(parent.FullName, "config.json"))
                ? parent.FullName : null;
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Bin);
        Directory.CreateDirectory(Tools);
        Directory.CreateDirectory(State);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Secrets);
        Directory.CreateDirectory(Temp);
    }

    public string ResolveUnderRoot(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Expected a path relative to the ToolDock root.");
        }

        var resolved = Path.GetFullPath(relativePath, Root);
        var prefix = Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Path escapes the ToolDock root.");
        }

        return resolved;
    }
}
