namespace ToolDock.Common;

public sealed class ToolDockPaths
{
    public ToolDockPaths(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Environment.GetEnvironmentVariable("TOOLDOCK_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToolDock"));
    }

    public string Root { get; }
    public string Bin => Path.Combine(Root, "bin");
    public string Tools => Path.Combine(Root, "tools");
    public string State => Path.Combine(Root, "state");
    public string Logs => Path.Combine(Root, "logs");
    public string Temp => Path.Combine(Root, "temp");
    public string ConfigFile => Path.Combine(Root, "config.json");
    public string InstalledStateFile => Path.Combine(State, "installed.json");
    public string CatalogCacheFile => Path.Combine(State, "catalog.json");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Bin);
        Directory.CreateDirectory(Tools);
        Directory.CreateDirectory(State);
        Directory.CreateDirectory(Logs);
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
