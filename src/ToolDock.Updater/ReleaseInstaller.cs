using System.IO.Compression;
using ToolDock.Common;
using ToolDock.Common.Logging;

namespace ToolDock.Updater;

internal sealed class ReleaseInstaller(HttpClient http, ToolDockPaths paths, ILog log)
{
    public async Task<InstalledTool> InstallAsync(
        string name,
        ToolDefinition definition,
        ReleaseAsset release,
        CancellationToken cancellationToken)
    {
        var version = Validation.ValidateVersion(release.Version);
        var toolRoot = Path.Combine(paths.Tools, name);
        var finalDirectory = Path.Combine(toolRoot, version);
        var stageDirectory = Path.Combine(toolRoot, $".install-{Guid.NewGuid():N}");
        var downloadPath = Path.Combine(paths.Temp, $"{name}-{Guid.NewGuid():N}.zip.part");
        var executableRelativePath = Validation.GetExecutablePath(name, definition);

        Directory.CreateDirectory(toolRoot);
        try
        {
            if (!Directory.Exists(finalDirectory))
            {
                log.Info($"downloading {name} {version}");
                await DownloadAsync(release.DownloadUri, downloadPath, cancellationToken);
                Directory.CreateDirectory(stageDirectory);
                ZipFile.ExtractToDirectory(downloadPath, stageDirectory);

                var stagedExecutable = ResolveUnder(stageDirectory, executableRelativePath);
                if (!File.Exists(stagedExecutable))
                {
                    throw new InvalidDataException(
                        $"Archive for {name} does not contain {executableRelativePath} at its root.");
                }

                Directory.Move(stageDirectory, finalDirectory);
            }

            var installedExecutable = ResolveUnder(finalDirectory, executableRelativePath);
            if (!File.Exists(installedExecutable))
            {
                throw new InvalidDataException(
                    $"Existing version directory for {name} {version} is incomplete; expected {executableRelativePath}.");
            }

            await Junction.SwitchAsync(Path.Combine(toolRoot, "current"), finalDirectory, cancellationToken);
            await WriteShimAsync(name, executableRelativePath, cancellationToken);

            return new InstalledTool
            {
                Version = release.Version,
                Path = Path.GetRelativePath(paths.Root, installedExecutable)
            };
        }
        finally
        {
            if (File.Exists(downloadPath))
            {
                File.Delete(downloadPath);
            }

            if (Directory.Exists(stageDirectory))
            {
                Directory.Delete(stageDirectory, recursive: true);
            }
        }
    }

    private async Task DownloadAsync(Uri uri, string destination, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(target, cancellationToken);
        await target.FlushAsync(cancellationToken);
    }

    private async Task WriteShimAsync(
        string name,
        string executableRelativePath,
        CancellationToken cancellationToken)
    {
        var target = Path.Combine("..", "tools", name, "current", executableRelativePath);
        var contents = $"@echo off\r\n\"%~dp0{target}\" %*\r\n";
        await AtomicFile.WriteTextAsync(Path.Combine(paths.Bin, $"{name}.cmd"), contents, cancellationToken);
    }

    private static string ResolveUnder(string root, string relativePath)
    {
        var resolved = Path.GetFullPath(relativePath, root);
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Executable path escapes the installed version directory.");
        }

        return resolved;
    }
}
