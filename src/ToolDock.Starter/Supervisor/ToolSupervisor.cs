using Microsoft.Extensions.Logging;
using ToolDock.Common;
using ToolDock.Starter.Processes;

namespace ToolDock.Starter.Supervisor;

internal sealed class ToolSupervisor(ToolDockPaths paths, ILogger<ToolSupervisor> log) : IAsyncDisposable
{
    private readonly Dictionary<string, ManagedToolProcess> _processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task AutostartAsync(CancellationToken cancellationToken)
    {
        log.LogInformation("Loading autostart configuration");
        var catalog = await JsonFiles.ReadOptionalAsync<ToolCatalog>(paths.CatalogCacheFile, cancellationToken);
        if (catalog is null)
        {
            log.LogWarning("Catalog cache is absent; waiting for updater");
            return;
        }

        Validation.ValidateCatalog(catalog);
        foreach (var (packageName, package) in catalog.Tools)
        {
            if (!package.Enabled)
            {
                continue;
            }

            foreach (var (daemonName, daemon) in package.Daemons)
            {
                if (!daemon.Autostart)
                {
                    continue;
                }

                try
                {
                    var response = await ExecuteAsync("start", daemonName, cancellationToken);
                    log.LogInformation("Autostart {Daemon}: {Response}", daemonName, response);
                }
                catch (Exception exception)
                {
                    log.LogError(exception, "Autostart failed for {Daemon}", daemonName);
                }
            }
        }
    }

    public async Task<string> ExecuteAsync(string command, string name, CancellationToken cancellationToken)
    {
        Validation.ValidateToolName(name);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return command switch
            {
                "start" => await StartCoreAsync(name, cancellationToken),
                "stop" => await StopCoreAsync(name),
                "restart" => await RestartCoreAsync(name, cancellationToken),
                "status" => StatusCore(name),
                _ => $"ERROR unknown command: {command}"
            };
        }
        catch (Exception exception)
        {
            log.LogError(exception, "{Command} failed for {Daemon}", command, name);
            return $"ERROR {SingleLine(exception.Message)}";
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> PackageUpdatedAsync(
        string packageName,
        bool firstInstall,
        CancellationToken cancellationToken)
    {
        Validation.ValidateToolName(packageName);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var catalog = await JsonFiles.ReadRequiredAsync<ToolCatalog>(paths.CatalogCacheFile, cancellationToken);
            Validation.ValidateCatalog(catalog);
            var package = Validation.FindDefinition(catalog, packageName);
            if (!package.Enabled)
            {
                return "OK package disabled";
            }

            var changed = new List<string>();
            foreach (var (daemonName, daemon) in package.Daemons)
            {
                if (firstInstall)
                {
                    if (daemon.Autostart)
                    {
                        var response = await StartCoreAsync(daemonName, cancellationToken);
                        if (!response.StartsWith("OK", StringComparison.Ordinal))
                        {
                            return response;
                        }
                        changed.Add(daemonName);
                    }
                }
                else if (daemon.RestartOnUpdate &&
                         _processes.TryGetValue(daemonName, out var process) &&
                         process.IsRunning)
                {
                    var response = await RestartCoreAsync(daemonName, cancellationToken);
                    if (!response.StartsWith("OK", StringComparison.Ordinal))
                    {
                        return response;
                    }
                    changed.Add(daemonName);
                }
            }

            return changed.Count == 0
                ? "OK no daemon changes"
                : $"OK {(firstInstall ? "started" : "restarted")}={string.Join(',', changed)}";
        }
        catch (Exception exception)
        {
            log.LogError(exception, "Package update reconciliation failed for {Package}", packageName);
            return $"ERROR {SingleLine(exception.Message)}";
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var catalog = await JsonFiles.ReadRequiredAsync<ToolCatalog>(paths.CatalogCacheFile, cancellationToken);
            Validation.ValidateCatalog(catalog);
            var names = catalog.Tools
                .SelectMany(pair => pair.Value.Daemons.Keys)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return names.Length == 0 ? "OK no daemons" : $"OK {string.Join(' ', names)}";
        }
        catch (Exception exception)
        {
            log.LogError(exception, "List failed");
            return $"ERROR {SingleLine(exception.Message)}";
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> StartCoreAsync(string name, CancellationToken cancellationToken)
    {
        if (_processes.TryGetValue(name, out var existing))
        {
            if (existing.IsRunning)
            {
                return $"OK running pid={existing.ProcessId}";
            }

            await existing.DisposeAsync();
            _processes.Remove(name);
        }

        var catalog = await JsonFiles.ReadRequiredAsync<ToolCatalog>(paths.CatalogCacheFile, cancellationToken);
        Validation.ValidateCatalog(catalog);
        var (packageName, package, daemon) = Validation.FindDaemon(catalog, name);
        if (!package.Enabled)
        {
            return $"ERROR package is disabled: {packageName}";
        }

        var state = await JsonFiles.ReadRequiredAsync<InstalledState>(paths.InstalledStateFile, cancellationToken);
        var installed = Validation.FindInstalled(state, packageName);
        var packageRoot = paths.ResolveUnderRoot(
            installed.Root ?? Path.Combine("tools", packageName, installed.Version));
        var executable = ResolveUnder(
            packageRoot,
            Validation.ValidateExecutablePath($"daemon {name}", daemon.Executable));
        if (!File.Exists(executable))
        {
            return $"ERROR installed executable is missing: {Path.GetRelativePath(paths.Root, executable)}";
        }

        log.LogInformation(
            "Starting {Daemon} from {Package} {Version}",
            name,
            packageName,
            installed.Version);
        var process = ManagedToolProcess.Start(
            name,
            executable,
            daemon.Arguments,
            Path.Combine(paths.Logs, $"{name}.log"));
        _processes.Add(name, process);
        return $"OK running pid={process.ProcessId}";
    }

    private async Task<string> StopCoreAsync(string name)
    {
        if (!_processes.Remove(name, out var process))
        {
            return "OK stopped";
        }

        log.LogInformation("Stopping {Daemon}", name);
        try
        {
            await process.StopAsync();
        }
        finally
        {
            await process.DisposeAsync();
        }
        return "OK stopped";
    }

    private async Task<string> RestartCoreAsync(string name, CancellationToken cancellationToken)
    {
        if (_processes.TryGetValue(name, out var process))
        {
            log.LogInformation("Restarting {Daemon}", name);
            _processes.Remove(name);
            try
            {
                await process.StopAsync();
            }
            finally
            {
                await process.DisposeAsync();
            }
        }

        return await StartCoreAsync(name, cancellationToken);
    }

    private string StatusCore(string name)
    {
        if (!_processes.TryGetValue(name, out var process))
        {
            return "OK stopped";
        }

        return process.IsRunning
            ? $"OK running pid={process.ProcessId}"
            : $"OK stopped exit={process.ExitCode}";
    }

    private static string ResolveUnder(string root, string relativePath)
    {
        var resolved = Path.GetFullPath(relativePath, root);
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Executable path escapes the installed package directory.");
        }

        return resolved;
    }

    private static string SingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ');

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var process in _processes.Values)
            {
                await process.DisposeAsync();
            }

            _processes.Clear();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
