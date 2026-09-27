namespace ToolDock.Common;

// Held for the whole command/daemon lifetime. Cleanup takes the same file exclusively.
public static class VersionLease
{
    public static string PathFor(string root)
    {
        var directory = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root))!, ".leases");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, ToolDockPaths.PathIdentity(root) + ".lock");
    }

    public static FileStream Acquire(string root)
    {
        var stream = new FileStream(PathFor(root), FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
        if (Directory.Exists(root)) return stream;
        stream.Dispose();
        throw new DirectoryNotFoundException($"Installed version is no longer available: {root}");
    }
}
