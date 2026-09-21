using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using ToolDock.Common;

namespace ToolDock.Updating;

internal sealed class UpdateEngine
{
    private readonly ToolDockPaths _paths;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<UpdateEngine> _log;

    public UpdateEngine(ToolDockPaths paths, ILoggerFactory loggerFactory)
    {
        _paths = paths;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<UpdateEngine>();
    }

    public async Task<UpdateResult> RunAsync(CancellationToken cancellationToken)
    {
        var config = await JsonFiles.ReadRequiredAsync<ToolDockConfig>(_paths.ConfigFile, cancellationToken);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ToolDock", "1.0"));

        _log.LogInformation("Fetching catalog");
        var catalog = await new CatalogClient(http).FetchAsync(config.CatalogUrl, cancellationToken);
        await JsonFiles.WriteAtomicAsync(_paths.CatalogCacheFile, catalog, cancellationToken);

        var state = await JsonFiles.ReadOptionalAsync<InstalledState>(
                _paths.InstalledStateFile,
                cancellationToken)
            ?? new InstalledState();
        var previousCommands = GetTrackedCommands(state);
        var releases = new GitHubReleaseClient(http);
        var installer = new ReleaseInstaller(
            http,
            _paths,
            _loggerFactory.CreateLogger<ReleaseInstaller>());
        var starter = new StarterClient();
        var checkedCount = 0;
        var updatedCount = 0;
        var failedCount = 0;

        foreach (var (name, definition) in catalog.Tools.OrderBy(
                     pair => pair.Key,
                     StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!definition.Enabled)
            {
                _log.LogInformation("Skipping disabled package {Package}", name);
                continue;
            }

            checkedCount++;
            try
            {
                var installed = FindInstalled(state, name);
                var release = await releases.GetLatestAsync(definition.Repo, definition.Asset, cancellationToken);
                var changed = installed is null ||
                    !string.Equals(installed.Version, release.Version, StringComparison.Ordinal);
                _log.LogInformation(
                    "Checking {Package}: installed={InstalledVersion} latest={LatestVersion}",
                    name,
                    installed?.Version ?? "none",
                    release.Version);

                var result = await installer.InstallAsync(name, definition, release, cancellationToken);
                Upsert(state, name, result);
                await JsonFiles.WriteAtomicAsync(_paths.InstalledStateFile, state, cancellationToken);
                if (!changed)
                {
                    continue;
                }

                updatedCount++;
                _log.LogInformation("Installed {Package} {Version}", name, result.Version);
                if (!await TryNotifyStarterAsync(
                        starter,
                        name,
                        installed is null ? "installed" : "updated",
                        cancellationToken))
                {
                    failedCount++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failedCount++;
                _log.LogError(exception, "Failed to update {Package}", name);
            }
        }

        foreach (var command in previousCommands.Except(
                     GetTrackedCommands(state),
                     StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                File.Delete(Path.Combine(_paths.Bin, $"{command}.cmd"));
                _log.LogInformation("Removed obsolete command {Command}", command);
            }
            catch (Exception exception)
            {
                failedCount++;
                _log.LogError(exception, "Failed to remove obsolete command {Command}", command);
            }
        }

        return new UpdateResult(
            failedCount == 0 ? UpdateStatus.Completed : UpdateStatus.CompletedWithErrors,
            checkedCount,
            updatedCount,
            failedCount);
    }

    private async Task<bool> TryNotifyStarterAsync(
        StarterClient starter,
        string name,
        string change,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await starter.SendAsync(
                $"package-updated {name} {change}",
                TimeSpan.FromMinutes(2),
                cancellationToken);
            if (response.StartsWith("OK", StringComparison.Ordinal))
            {
                _log.LogInformation("Starter replied: {Response}", response);
                return true;
            }

            _log.LogWarning(
                "Starter rejected package update for {Package}: {Response}",
                name,
                response);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log.LogWarning(
                exception,
                "Installed {Package}, but starter is unavailable",
                name);
            return false;
        }
    }

    private static InstalledTool? FindInstalled(InstalledState state, string name)
        => state.Tools.FirstOrDefault(
            pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static void Upsert(InstalledState state, string name, InstalledTool tool)
    {
        var existingName = state.Tools.Keys.FirstOrDefault(
            key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        if (existingName is not null && !string.Equals(existingName, name, StringComparison.Ordinal))
        {
            state.Tools.Remove(existingName);
        }

        state.Tools[name] = tool;
    }

    private static HashSet<string> GetTrackedCommands(InstalledState state)
    {
        var commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (packageName, package) in state.Tools)
        {
            if (!package.EntryPointsTracked)
            {
                commands.Add(packageName);
                continue;
            }

            commands.UnionWith(package.Commands);
        }

        return commands;
    }
}
