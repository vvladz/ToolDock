using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ToolDock.Common;

namespace ToolDock.Updating;

internal sealed class VersionRetention(ToolDockPaths paths, ILogger log)
{
    internal const int RetainedVersions = 3;

    public async Task PruneAsync(string package, InstalledTool installed, CancellationToken cancellationToken)
    {
        try
        {
            var root = Path.Combine(paths.Tools, package);
            if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) return;
            var activeRoot = paths.ResolveUnderRoot(installed.Root ?? Path.Combine("tools", package, installed.Version));
            var current = new DirectoryInfo(Path.Combine(root, "current")).ResolveLinkTarget(true)?.FullName;
            var versions = new List<(DirectoryInfo Directory, VersionMarker Marker)>();
            foreach (var directory in new DirectoryInfo(root).EnumerateDirectories())
            {
                if (directory.Name.StartsWith('.') || directory.Name.Equals("current", StringComparison.OrdinalIgnoreCase) ||
                    directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var marker = await JsonFiles.ReadOptionalAsync<VersionMarker>(Path.Combine(directory.FullName, ReleaseInstaller.MarkerName), cancellationToken);
                if (marker is not null) versions.Add((directory, marker));
            }
            foreach (var (candidate, _) in versions.OrderByDescending(v => v.Marker.InstalledAt).Skip(RetainedVersions))
            {
                var directory = candidate;
                cancellationToken.ThrowIfCancellationRequested();
                if (directory.FullName.Equals(activeRoot, StringComparison.OrdinalIgnoreCase) ||
                    directory.FullName.Equals(current, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (ContainsReparsePoint(directory) || MayHaveRunningExecutable(directory)) continue;
                    var leasePath = VersionLease.PathFor(directory.FullName);
                    using (var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        // Unpublish the path while launches are excluded by the exclusive lease.
                        var retired = Path.Combine(root, $".cleanup-{Guid.NewGuid():N}");
                        Directory.Move(directory.FullName, retired);
                        directory = new DirectoryInfo(retired);
                    }
                    DeleteTree(directory);
                    log.LogInformation("Removed unused version directory {Directory}", directory.FullName);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    log.LogWarning(exception, "Version cleanup deferred for {Package}", package);
                }
            }
            // Only our retired directories are retried; never traverse staging or current.
            foreach (var retired in new DirectoryInfo(root).EnumerateDirectories(".cleanup-*"))
            {
                if (!Guid.TryParseExact(retired.Name[9..], "N", out _) ||
                    !File.Exists(Path.Combine(retired.FullName, ReleaseInstaller.MarkerName)) ||
                    retired.Attributes.HasFlag(FileAttributes.ReparsePoint) || ContainsReparsePoint(retired) ||
                    MayHaveRunningExecutable(retired)) continue;
                try { DeleteTree(retired); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { log.LogWarning(exception, "Retired version cleanup deferred for {Package}", package); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { log.LogWarning(exception, "Version cleanup deferred for {Package}", package); }
    }

    private static bool ContainsReparsePoint(DirectoryInfo directory)
        => directory.EnumerateFileSystemInfos().Any(entry => entry.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            entry is DirectoryInfo child && ContainsReparsePoint(child));

    private static bool MayHaveRunningExecutable(DirectoryInfo directory)
    {
        // Also protect a command whose client died before its child. Inaccessible matching processes are retained.
        // CreateProcess can execute binaries with extensions other than .exe.
        var names = directory.EnumerateFiles("*", SearchOption.AllDirectories)
            .SelectMany(f => new[] { f.Name, Path.GetFileNameWithoutExtension(f.Name) }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (!names.Contains(process.ProcessName)) continue;
                    var executable = process.MainModule?.FileName;
                    if (executable is null || executable.StartsWith(directory.FullName + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
                { if (!process.HasExited) return true; }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        return false;
    }

    private static void DeleteTree(DirectoryInfo directory)
    {
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Refusing to traverse a reparse point.");
        // Keep the ownership marker until all payload files have been removed, so interrupted cleanup is retryable.
        foreach (var entry in directory.EnumerateFileSystemInfos().OrderBy(e => e.Name == ReleaseInstaller.MarkerName))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Refusing to traverse a reparse point.");
            if (entry is DirectoryInfo child) DeleteTree(child);
            else entry.Delete();
        }
        directory.Delete();
    }
}
