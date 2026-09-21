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
        foreach (var (name, definition) in catalog.Tools)
        {
            if (!definition.Enabled || !definition.Autostart)
            {
                continue;
            }

            try
            {
                var response = await ExecuteAsync("start", name, cancellationToken);
                log.LogInformation("Autostart {Tool}: {Response}", name, response);
            }
            catch (Exception exception)
            {
                log.LogError(exception, "Autostart failed for {Tool}", name);
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
            log.LogError(exception, "{Command} failed for {Tool}", command, name);
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
            var names = catalog.Tools.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            return names.Length == 0 ? "OK no tools" : $"OK {string.Join(' ', names)}";
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
        var definition = Validation.FindDefinition(catalog, name);
        if (!definition.Enabled)
        {
            return $"ERROR tool is disabled: {name}";
        }

        var state = await JsonFiles.ReadRequiredAsync<InstalledState>(paths.InstalledStateFile, cancellationToken);
        var installed = Validation.FindInstalled(state, name);
        var executable = paths.ResolveUnderRoot(installed.Path);
        if (!File.Exists(executable))
        {
            return $"ERROR installed executable is missing: {installed.Path}";
        }

        log.LogInformation("Starting {Tool} {Version}", name, installed.Version);
        var process = ManagedToolProcess.Start(name, executable, Path.Combine(paths.Logs, $"{name}.log"));
        _processes.Add(name, process);
        return $"OK running pid={process.ProcessId}";
    }

    private async Task<string> StopCoreAsync(string name)
    {
        if (!_processes.Remove(name, out var process))
        {
            return "OK stopped";
        }

        log.LogInformation("Stopping {Tool}", name);
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
            log.LogInformation("Restarting {Tool}", name);
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
