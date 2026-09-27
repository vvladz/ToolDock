using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using ToolDock.Common;

namespace ToolDock.Updating;

internal sealed class ReleaseInstaller(HttpClient http, ToolDockPaths paths, ILogger<ReleaseInstaller> log)
{
    internal const string MarkerName = ".tooldock-version.json";

    public async Task<InstalledTool> StageAsync(string name, ToolDefinition definition, ReleaseAsset release,
        CancellationToken cancellationToken)
    {
        Validation.ValidateToolName(name);
        var version = Validation.ValidateVersion(release.Version);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            definition.Repo.ToUpperInvariant() + "\n" + definition.Asset)))[..12];
        var toolRoot = Path.Combine(paths.Tools, name);
        var finalDirectory = Path.Combine(toolRoot, $"{version}--{identity}");
        var stageDirectory = Path.Combine(toolRoot, $".install-{Guid.NewGuid():N}");
        var downloadPath = Path.Combine(paths.Temp, $"{name}-{Guid.NewGuid():N}.zip.part");
        Directory.CreateDirectory(toolRoot);
        try
        {
            var marker = await JsonFiles.ReadOptionalAsync<VersionMarker>(Path.Combine(finalDirectory, MarkerName), cancellationToken);
            if (marker is null || marker.Version != version ||
                !string.Equals(marker.Repository, definition.Repo, StringComparison.OrdinalIgnoreCase) || marker.Asset != definition.Asset)
            {
                // Never replace an existing directory that may still be used by a process.
                if (Directory.Exists(finalDirectory)) finalDirectory += "-" + Guid.NewGuid().ToString("N");
                log.LogInformation("Downloading {Package} {Version}", name, version);
                using var response = await http.GetAsync(release.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var target = new FileStream(downloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await source.CopyToAsync(target, cancellationToken);
                }
                Directory.CreateDirectory(stageDirectory);
                ZipFile.ExtractToDirectory(downloadPath, stageDirectory);
                ValidateExecutables(name, stageDirectory, definition);
                await JsonFiles.WriteAtomicAsync(Path.Combine(stageDirectory, MarkerName), new VersionMarker(
                    version, definition.Repo, definition.Asset, DateTimeOffset.UtcNow), cancellationToken);
                Directory.Move(stageDirectory, finalDirectory);
            }
            ValidateExecutables(name, finalDirectory, definition);
            return new InstalledTool
            {
                Version = version,
                Root = Path.GetRelativePath(paths.Root, finalDirectory),
                Repository = definition.Repo,
                Asset = definition.Asset,
                EntryPointsTracked = true,
                Commands = definition.Commands.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList()
            };
        }
        finally
        {
            try
            {
                if (File.Exists(downloadPath)) File.Delete(downloadPath);
                if (Directory.Exists(stageDirectory)) Directory.Delete(stageDirectory, recursive: true);
            }
            catch (IOException exception) { log.LogWarning(exception, "Could not remove temporary download for {Package}", name); }
        }
    }

    internal static void ValidateExecutables(string name, string root, ToolDefinition definition)
    {
        foreach (var relative in definition.Commands.Values.Select(c => c.Executable)
                     .Concat(definition.Daemons.Values.Select(d => d.Executable)))
        {
            if (!File.Exists(Validation.ResolveExecutable(root, name, relative)))
                throw new InvalidDataException($"Package {name} does not contain {relative}.");
        }
    }
}

internal sealed record VersionMarker(string Version, string Repository, string Asset, DateTimeOffset InstalledAt);
