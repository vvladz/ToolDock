using System.Net.Http.Headers;
using ToolDock.Common;
using ToolDock.Common.Logging;

namespace ToolDock.Updater;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var attached = ConsoleHost.TryAttachParent();
        var paths = new ToolDockPaths();
        paths.EnsureDirectories();
        using var log = new ToolDockLog(attached, Path.Combine(paths.Logs, "updater.log"));

        if (args is ["--help" or "-h"])
        {
            log.Info("Usage: updater.exe");
            return 0;
        }

        if (args.Length != 0)
        {
            log.Error("Updater does not accept arguments. Use --help for usage.");
            return 2;
        }

        using var mutex = new Mutex(initiallyOwned: false, @"Local\ToolDock.Updater");
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            if (!ownsMutex)
            {
                if (attached)
                {
                    log.Warning("another updater instance is already running");
                }
                return 0;
            }

            return await RunAsync(paths, log, CancellationToken.None);
        }
        catch (Exception exception)
        {
            log.Error("update failed", exception);
            return 1;
        }
        finally
        {
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static async Task<int> RunAsync(ToolDockPaths paths, ILog log, CancellationToken cancellationToken)
    {
        var config = await JsonFiles.ReadRequiredAsync<ToolDockConfig>(paths.ConfigFile, cancellationToken);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ToolDock", "1.0"));

        log.Info("fetching catalog");
        var catalog = await new CatalogClient(http).FetchAsync(config.CatalogUrl, cancellationToken);
        await JsonFiles.WriteAtomicAsync(paths.CatalogCacheFile, catalog, cancellationToken);

        var state = await JsonFiles.ReadOptionalAsync<InstalledState>(paths.InstalledStateFile, cancellationToken)
            ?? new InstalledState();
        var releases = new GitHubReleaseClient(http);
        var installer = new ReleaseInstaller(http, paths, log);
        var starter = new StarterClient();
        var failed = false;

        foreach (var (name, definition) in catalog.Tools.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!definition.Enabled)
            {
                log.Info($"skipping disabled tool {name}");
                continue;
            }

            try
            {
                var installed = FindInstalled(state, name);
                var release = await releases.GetLatestAsync(definition.Repo, definition.Asset, cancellationToken);
                log.Info($"checking {name}: installed={installed?.Version ?? "none"} latest={release.Version}");
                if (installed is not null && string.Equals(installed.Version, release.Version, StringComparison.Ordinal))
                {
                    continue;
                }

                var result = await installer.InstallAsync(name, definition, release, cancellationToken);
                Upsert(state, name, result);
                await JsonFiles.WriteAtomicAsync(paths.InstalledStateFile, state, cancellationToken);
                log.Info($"installed {name} {result.Version}");

                var lifecycleCommand = installed is null
                    ? definition.Autostart ? "start" : null
                    : definition.Restart ? "restart" : null;
                if (lifecycleCommand is not null)
                {
                    await TryNotifyStarterAsync(starter, lifecycleCommand, name, log, cancellationToken);
                }
            }
            catch (Exception exception)
            {
                failed = true;
                log.Error($"failed to update {name}", exception);
            }
        }

        return failed ? 1 : 0;
    }

    private static InstalledTool? FindInstalled(InstalledState state, string name)
        => state.Tools.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static void Upsert(InstalledState state, string name, InstalledTool tool)
    {
        var existingName = state.Tools.Keys.FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        if (existingName is not null && !string.Equals(existingName, name, StringComparison.Ordinal))
        {
            state.Tools.Remove(existingName);
        }

        state.Tools[name] = tool;
    }

    private static async Task TryNotifyStarterAsync(
        StarterClient starter,
        string command,
        string name,
        ILog log,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await starter.SendAsync($"{command} {name}", TimeSpan.FromSeconds(5), cancellationToken);
            if (response.StartsWith("OK", StringComparison.Ordinal))
            {
                log.Info($"starter replied: {response}");
            }
            else
            {
                log.Warning($"starter rejected {command} {name}: {response}");
            }
        }
        catch (Exception exception)
        {
            log.Warning($"installed {name}, but starter is unavailable: {exception.Message}");
        }
    }
}
