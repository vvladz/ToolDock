using System.IO.Compression;
using Microsoft.Extensions.Logging;
using ToolDock.Common;

namespace ToolDock.Updating;

internal sealed class ReleaseInstaller(
    HttpClient http,
    ToolDockPaths paths,
    ILogger<ReleaseInstaller> log)
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
        var commands = definition.Commands;
        var daemons = definition.Daemons;
        var executables = commands.Values
            .Concat(daemons.Values.Select(daemon => daemon.Executable))
            .Select(path => Validation.ValidateExecutablePath(name, path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Directory.CreateDirectory(toolRoot);
        try
        {
            if (!Directory.Exists(finalDirectory))
            {
                log.LogInformation("Downloading {Tool} {Version}", name, version);
                await DownloadAsync(release.DownloadUri, downloadPath, cancellationToken);
                Directory.CreateDirectory(stageDirectory);
                ZipFile.ExtractToDirectory(downloadPath, stageDirectory);

                ValidateExecutables(name, stageDirectory, executables);

                Directory.Move(stageDirectory, finalDirectory);
            }

            ValidateExecutables(name, finalDirectory, executables);

            await Junction.SwitchAsync(Path.Combine(toolRoot, "current"), finalDirectory, cancellationToken);
            foreach (var (commandName, executable) in commands)
            {
                await WriteShimAsync(name, commandName, executable, cancellationToken);
            }

            return new InstalledTool
            {
                Version = release.Version,
                Root = Path.GetRelativePath(paths.Root, finalDirectory),
                EntryPointsTracked = true,
                Commands = commands.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList()
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
        string packageName,
        string commandName,
        string executableRelativePath,
        CancellationToken cancellationToken)
    {
        var target = Path.Combine("..", "tools", packageName, "current", executableRelativePath);
        var contents = $"@echo off\r\n\"%~dp0{target}\" %*\r\n";
        await AtomicFile.WriteTextAsync(Path.Combine(paths.Bin, $"{commandName}.cmd"), contents, cancellationToken);
    }

    private static void ValidateExecutables(string packageName, string root, IEnumerable<string> executables)
    {
        foreach (var executable in executables)
        {
            if (!File.Exists(ResolveUnder(root, executable)))
            {
                throw new InvalidDataException($"Package {packageName} does not contain {executable}.");
            }
        }
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
